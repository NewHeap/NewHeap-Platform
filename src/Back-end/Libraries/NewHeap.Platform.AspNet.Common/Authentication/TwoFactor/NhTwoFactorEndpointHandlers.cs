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
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using System.Security.Claims;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Base class for anonymous endpoints that continue a pending challenge or enrollment.
/// </summary>
public abstract class NhTwoFactorStepEndpointHandler : BaseNhAuthenticationEndpoint, IConfigurableAuthenticationEndpoint
{
    protected NhTwoFactorStepEndpointHandler(
        IHttpContextAccessor httpContextAccessor,
        string pattern,
        AuthenticationConfiguration configuration)
        : base(httpContextAccessor, pattern, configuration)
    {
    }

    public virtual void Configure(RouteHandlerBuilder builder)
    {
        builder
            .AllowAnonymous()
            .Produces<Dictionary<string, string[]>>(StatusCodes.Status400BadRequest);
    }

    protected INhMultiFactorAuthenticationService? MultiFactorService
    {
        get
        {
            if (GetAuthService() is INhMultiFactorAuthenticationService { IsTwoFactorAvailable: true } multiFactorService)
            {
                return multiFactorService;
            }

            return null;
        }
    }

    protected IResult ConfigurationInvalid()
    {
        return BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ConfigurationInvalid));
    }

    /// <summary>
    /// Writes the remember-device cookie for cookie clients. The token is also returned in the
    /// response body for clients that send the <c>Authorization</c> header.
    /// </summary>
    protected void WriteRememberDeviceCookie(string rememberDeviceToken)
    {
        var configuration = HttpContext!.RequestServices.GetRequiredService<NhTwoFactorConfiguration>();
        HttpContext.Response.Cookies.Append(configuration.RememberDeviceCookieName, rememberDeviceToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.Add(configuration.RememberDeviceLifetime),
            IsEssential = true,
        });
    }

    protected IResult EnrollmentCompleted(NhTwoFactorEnrollmentCompletion completion)
    {
        WriteTokenToCookie(completion.Session);

        return TypedResults.Ok(new NhTwoFactorChangeResponse
        {
            RecoveryCodes = completion.RecoveryCodes,
            Session = completion.Session,
        });
    }

    protected static TaskResult RequireFields(params (string Name, string? Value)[] fields)
    {
        var result = new TaskResult();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Value))
            {
                result.AddError(field.Name, "Field is required");
            }
        }

        return result;
    }
}

/// <summary>
/// Completes a second-factor challenge and issues the session.
/// </summary>
public class NhTwoFactorVerifyAuthenticationHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorVerifyAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/verify", configuration)
    {
        Handler = VerifyAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Complete a two-factor challenge")
            .WithDescription(
                "Verifies an authenticator, e-mail or recovery code for the challenge returned by the login endpoint " +
                "and issues the access and refresh tokens. Wrong codes count towards the account lockout. With " +
                "rememberDevice the response also sets a remember-device cookie and returns its token.")
            .Produces<NhLoginResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Verify two-factor challenge")]
    private async Task<IResult> VerifyAsync([FromBody] NhTwoFactorVerifyRequest? request)
    {
        var validation = RequireFields(
            (nameof(NhTwoFactorVerifyRequest.ChallengeToken), request?.ChallengeToken),
            (nameof(NhTwoFactorVerifyRequest.Method), request?.Method),
            (nameof(NhTwoFactorVerifyRequest.Code), request?.Code));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.VerifyTwoFactorAsync(request!, Configuration.AuthenticateRequiredClaims);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        var session = result.Data!.Session!;
        WriteTokenToCookie(session);

        if (result.Data.RememberDeviceToken != null)
        {
            WriteRememberDeviceCookie(result.Data.RememberDeviceToken);
        }

        return TypedResults.Ok(NhLoginResponse.FromSession(session, result.Data.RememberDeviceToken));
    }
}

/// <summary>
/// E-mails a sign-in code for a pending challenge.
/// </summary>
public class NhTwoFactorEmailCodeAuthenticationHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEmailCodeAuthenticationHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/email", configuration)
    {
        Handler = SendAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("E-mail a sign-in code")
            .WithDescription(
                "Sends a single-use code to the confirmed e-mail address of the user behind the challenge. The code " +
                "only completes that challenge; a new request replaces the previous code after the resend cooldown.")
            .Produces<NhTwoFactorEmailCodeSentResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("E-mail two-factor sign-in code")]
    private async Task<IResult> SendAsync([FromBody] NhTwoFactorChallengeRequest? request)
    {
        var validation = RequireFields((nameof(NhTwoFactorChallengeRequest.ChallengeToken), request?.ChallengeToken));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.SendTwoFactorEmailCodeAsync(request!.ChallengeToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Starts authenticator enrollment for a user whom the policy requires to enroll during sign-in.
/// </summary>
public class NhTwoFactorEnrollmentAuthenticatorSetupHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEnrollmentAuthenticatorSetupHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/enrollment/authenticator", configuration)
    {
        Handler = BeginAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start required authenticator enrollment")
            .WithDescription(
                "Creates a pending authenticator key for the user behind the enrollment token and returns it with an " +
                "otpauth URI and QR code.")
            .Produces<NhAuthenticatorSetupViewModel>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Start required authenticator enrollment")]
    private async Task<IResult> BeginAsync([FromBody] NhTwoFactorEnrollmentRequest? request)
    {
        var validation = RequireFields((nameof(NhTwoFactorEnrollmentRequest.EnrollmentToken), request?.EnrollmentToken));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.BeginEnrollmentAuthenticatorSetupAsync(request!.EnrollmentToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Confirms the authenticator during a required enrollment and issues the session.
/// </summary>
public class NhTwoFactorEnrollmentAuthenticatorConfirmHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEnrollmentAuthenticatorConfirmHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/enrollment/authenticator/confirm", configuration)
    {
        Handler = ConfirmAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Confirm required authenticator enrollment")
            .WithDescription(
                "Verifies a code from the authenticator app, enables two-factor authentication, returns the recovery " +
                "codes once and issues the session.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Confirm required authenticator enrollment")]
    private async Task<IResult> ConfirmAsync([FromBody] NhTwoFactorEnrollmentConfirmRequest? request)
    {
        var validation = RequireFields(
            (nameof(NhTwoFactorEnrollmentConfirmRequest.EnrollmentToken), request?.EnrollmentToken),
            (nameof(NhTwoFactorEnrollmentConfirmRequest.Code), request?.Code));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.ConfirmEnrollmentAuthenticatorAsync(request!.EnrollmentToken, request.Code);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return EnrollmentCompleted(result.Data!);
    }
}

/// <summary>
/// E-mails a confirmation code during a required enrollment.
/// </summary>
public class NhTwoFactorEnrollmentEmailHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEnrollmentEmailHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/enrollment/email", configuration)
    {
        Handler = SendAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("E-mail a required enrollment code")
            .WithDescription(
                "Sends a code that confirms the e-mail address as the second factor during a required enrollment. " +
                "Only available when e-mail codes satisfy the requirement.")
            .Produces<NhTwoFactorEmailCodeSentResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("E-mail required enrollment code")]
    private async Task<IResult> SendAsync([FromBody] NhTwoFactorEnrollmentRequest? request)
    {
        var validation = RequireFields((nameof(NhTwoFactorEnrollmentRequest.EnrollmentToken), request?.EnrollmentToken));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.SendEnrollmentEmailCodeAsync(request!.EnrollmentToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Confirms the e-mail factor during a required enrollment and issues the session.
/// </summary>
public class NhTwoFactorEnrollmentEmailConfirmHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEnrollmentEmailConfirmHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/enrollment/email/confirm", configuration)
    {
        Handler = ConfirmAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Confirm required e-mail enrollment")
            .WithDescription(
                "Verifies the e-mailed code, enables the e-mail factor, returns the recovery codes once and issues the session.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Confirm required e-mail enrollment")]
    private async Task<IResult> ConfirmAsync([FromBody] NhTwoFactorEnrollmentConfirmRequest? request)
    {
        var validation = RequireFields(
            (nameof(NhTwoFactorEnrollmentConfirmRequest.EnrollmentToken), request?.EnrollmentToken),
            (nameof(NhTwoFactorEnrollmentConfirmRequest.Code), request?.Code));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.ConfirmEnrollmentEmailAsync(request!.EnrollmentToken, request.Code);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return EnrollmentCompleted(result.Data!);
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

    protected bool IsImpersonating =>
        HttpContext!.User.HasClaim(x => x.Type == NhPlatformClaimTypes.ImpersonateOriginUserId);

    /// <summary>
    /// Loads the signed-in user. Changes are refused while an administrator impersonates the
    /// user, because the administrator must not control the user's second factor.
    /// </summary>
    protected async Task<CurrentUserResult> GetCurrentUserAsync(bool forChange)
    {
        if (forChange && IsImpersonating)
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
    private async Task<IResult> ConfirmAsync([FromBody] NhTwoFactorCodeRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Code))
        {
            return BadRequest(TaskResult.Failed(nameof(NhTwoFactorCodeRequest.Code), "Field is required"));
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
/// E-mails a code that confirms the e-mail address as a second factor.
/// </summary>
public class NhTwoFactorEmailSetupEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorEmailSetupEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/email", HttpMethod.Post, configuration)
    {
        Handler = BeginSetupAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start e-mail factor enrollment")
            .WithDescription(
                "E-mails a code to the confirmed e-mail address. Confirming it adds the e-mail address as a second factor. " +
                "Requires reauthentication while two-factor authentication is enabled.")
            .Produces<NhTwoFactorEmailCodeSentResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Start e-mail factor enrollment")]
    private async Task<IResult> BeginSetupAsync([FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.BeginEmailSetupAsync(
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
/// Confirms the e-mail factor.
/// </summary>
public class NhTwoFactorEmailConfirmEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorEmailConfirmEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/email/confirm", HttpMethod.Post, configuration)
    {
        Handler = ConfirmAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Confirm e-mail factor enrollment")
            .WithDescription(
                "Verifies the e-mailed code and adds the e-mail address as a second factor. Every other session ends; " +
                "the response contains a new session for this device.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Confirm e-mail factor enrollment")]
    private async Task<IResult> ConfirmAsync([FromBody] NhTwoFactorCodeRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Code))
        {
            return BadRequest(TaskResult.Failed(nameof(NhTwoFactorCodeRequest.Code), "Field is required"));
        }

        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.ConfirmEmailSetupAsync(
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

/// <summary>
/// Forgets every remembered device of the signed-in user.
/// </summary>
public class NhTwoFactorForgetDevicesEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorForgetDevicesEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/two-factor/forget-devices", HttpMethod.Post, configuration)
    {
        Handler = ForgetAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Forget remembered devices")
            .WithDescription(
                "Requires reauthentication. Every device asks for the second factor again and every other session ends; " +
                "the response contains a new session for this device.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Forget remembered devices")]
    private async Task<IResult> ForgetAsync([FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.ForgetDevicesAsync(
            current.User!,
            request?.Reauthentication,
            current.User!.Id,
            HttpContext!.RequestAborted);

        return await ChangeResponseAsync(result);
    }
}

/// <summary>
/// Base class for the two-factor administration endpoints. They are only available when an
/// administration policy was configured with <see cref="NhTwoFactorBuilder.UseAdministrationPolicy"/>.
/// </summary>
public abstract class NhTwoFactorAdministrationEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    private readonly NhTwoFactorConfiguration _twoFactorConfiguration;

    protected NhTwoFactorAdministrationEndpointHandler(
        IHttpContextAccessor httpContextAccessor,
        string pattern,
        AuthenticationConfiguration configuration,
        NhTwoFactorConfiguration twoFactorConfiguration)
        : base(httpContextAccessor, pattern, HttpMethod.Post, configuration)
    {
        _twoFactorConfiguration = twoFactorConfiguration;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        var authorize = new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme };
        if (_twoFactorConfiguration.AdministrationPolicy != null)
        {
            authorize.Policy = _twoFactorConfiguration.AdministrationPolicy;
        }

        builder
            .RequireAuthorization(authorize)
            .Produces<Dictionary<string, string[]>>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }

    protected bool AdministrationEnabled => _twoFactorConfiguration.AdministrationPolicy != null;
}

/// <summary>
/// Resets every second factor of a user who lost access to them.
/// </summary>
public class NhTwoFactorResetEndpointHandler<TUser> : NhTwoFactorAdministrationEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorResetEndpointHandler(
        AuthenticationConfiguration configuration,
        NhTwoFactorConfiguration twoFactorConfiguration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/users/{userId:guid}/reset", configuration, twoFactorConfiguration)
    {
        Handler = ResetAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Reset a user's two-factor authentication")
            .WithDescription(
                "Removes every second factor of the user, ends every session and notifies the user. A user whom the " +
                "policy requires to use a second factor enrolls again at the next sign-in.")
            .Produces(StatusCodes.Status204NoContent);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account administration")]
    [EndpointName("Reset two-factor authentication")]
    private async Task<IResult> ResetAsync([FromRoute] Guid userId)
    {
        if (!AdministrationEnabled)
        {
            return TypedResults.NotFound();
        }

        if (IsImpersonating)
        {
            return BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.NotAllowedWhileImpersonating));
        }

        var userManager = HttpContext!.RequestServices.GetRequiredService<UserManager<TUser>>();
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return TypedResults.NotFound();
        }

        var result = await TwoFactorService.ResetAsync(user, CurrentUserId, HttpContext.RequestAborted);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.NoContent();
    }
}

/// <summary>
/// Starts a background operation that reminds every user whom the policy requires to enroll.
/// </summary>
public class NhTwoFactorEnrollmentRemindersEndpointHandler<TUser> : NhTwoFactorAdministrationEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorEnrollmentRemindersEndpointHandler(
        AuthenticationConfiguration configuration,
        NhTwoFactorConfiguration twoFactorConfiguration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/operations/enrollment-reminders", configuration, twoFactorConfiguration)
    {
        Handler = EnqueueAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Remind users to enroll a second factor")
            .WithDescription(
                "Starts a background operation that notifies every user whom the two-factor policy requires to enroll. " +
                "Requires AddTwoFactorOperations on the background-operation builder.")
            .Produces<NhBackgroundOperationViewModel>(StatusCodes.Status202Accepted);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account administration")]
    [EndpointName("Remind users to enroll a second factor")]
    private async Task<IResult> EnqueueAsync([FromBody] NhTwoFactorOperationRequest? request)
    {
        if (!AdministrationEnabled)
        {
            return TypedResults.NotFound();
        }

        if (IsImpersonating)
        {
            return BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.NotAllowedWhileImpersonating));
        }

        return await NhTwoFactorOperationEndpoint.EnqueueCoreAsync(
            HttpContext!,
            CurrentUserId,
            request,
            userId => new NhTwoFactorEnrollmentReminderRequest(userId),
            BadRequest);
    }
}

/// <summary>
/// Starts a background operation that ends the sessions of every user whom the policy
/// requires to enroll, so they enroll at their next sign-in.
/// </summary>
public class NhTwoFactorSessionRevocationEndpointHandler<TUser> : NhTwoFactorAdministrationEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhTwoFactorSessionRevocationEndpointHandler(
        AuthenticationConfiguration configuration,
        NhTwoFactorConfiguration twoFactorConfiguration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/operations/session-revocation", configuration, twoFactorConfiguration)
    {
        Handler = EnqueueAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("End sessions of users who must enroll")
            .WithDescription(
                "Starts a background operation that ends every session of the users whom the two-factor policy requires " +
                "to enroll. Requires AddTwoFactorOperations on the background-operation builder.")
            .Produces<NhBackgroundOperationViewModel>(StatusCodes.Status202Accepted);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account administration")]
    [EndpointName("End sessions of users who must enroll")]
    private async Task<IResult> EnqueueAsync([FromBody] NhTwoFactorOperationRequest? request)
    {
        if (!AdministrationEnabled)
        {
            return TypedResults.NotFound();
        }

        if (IsImpersonating)
        {
            return BadRequest(NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.NotAllowedWhileImpersonating));
        }

        return await NhTwoFactorOperationEndpoint.EnqueueCoreAsync(
            HttpContext!,
            CurrentUserId,
            request,
            userId => new NhTwoFactorSessionRevocationRequest(userId),
            BadRequest);
    }
}

internal static class NhTwoFactorOperationEndpoint
{
    internal static async Task<IResult> EnqueueCoreAsync<TRequest>(
        HttpContext httpContext,
        Guid? actorUserId,
        NhTwoFactorOperationRequest? request,
        Func<Guid, TRequest> createRequest,
        Func<TaskResult, IResult> reject)
    {
        if (string.IsNullOrWhiteSpace(request?.IdempotencyKey))
        {
            return reject(TaskResult.Failed(nameof(NhTwoFactorOperationRequest.IdempotencyKey), "Field is required"));
        }

        if (!actorUserId.HasValue)
        {
            return TypedResults.Unauthorized();
        }

        var operations = httpContext.RequestServices.GetService<INhBackgroundOperationService>();
        if (operations == null)
        {
            return TypedResults.NotFound();
        }

        var result = await operations.EnqueueAsync(
            createRequest(actorUserId.Value),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = actorUserId.Value,
                IdempotencyKey = request.IdempotencyKey.Trim(),
                DomainObjectType = "two-factor-policy",
            },
            httpContext.RequestAborted);

        if (!result.Success)
        {
            return reject(result);
        }

        return TypedResults.Accepted((string?)null, result.Data);
    }
}
