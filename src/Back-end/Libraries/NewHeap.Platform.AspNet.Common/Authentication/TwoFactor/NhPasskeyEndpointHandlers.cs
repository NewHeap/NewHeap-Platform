using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using System.Text.Json;
using HttpMethod = NewHeap.Platform.AspNet.Common.Builders.HttpMethod;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Creates request options for a passwordless sign-in with a discoverable passkey.
/// </summary>
public class NhPasskeySignInOptionsHandler : NhTwoFactorStepEndpointHandler
{
    public NhPasskeySignInOptionsHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/passkey/options", configuration)
    {
        Handler = BeginAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start a passkey sign-in")
            .WithDescription(
                "Returns WebAuthn request options without naming a user, so the browser offers the discoverable " +
                "passkeys for this site. Send the assertion with the ceremony token to the passkey login endpoint.")
            .Produces<NhPasskeyOptionsResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Start passkey sign-in")]
    private async Task<IResult> BeginAsync()
    {
        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.BeginPasskeySignInAsync(HttpContext!);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Signs in with a passkey.
/// </summary>
public class NhPasskeySignInHandler : NhTwoFactorStepEndpointHandler
{
    public NhPasskeySignInHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/passkey/login", configuration)
    {
        Handler = SignInAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Sign in with a passkey")
            .WithDescription(
                "Verifies the passkey assertion for the ceremony token and issues the access and refresh tokens. A " +
                "passkey verifies the user on the authenticator, so it satisfies the two-factor requirement unless the " +
                "application configured otherwise; then the response contains a pending two-factor step.")
            .Produces<NhLoginResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Sign in with passkey")]
    private async Task<IResult> SignInAsync([FromBody] NhPasskeySignInRequest? request)
    {
        var validation = RequireFields((nameof(NhPasskeySignInRequest.CeremonyToken), request?.CeremonyToken));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var configuration = HttpContext!.RequestServices.GetRequiredService<NhTwoFactorConfiguration>();
        if (string.IsNullOrEmpty(request!.RememberDeviceToken) && configuration.RememberDeviceEnabled)
        {
            // Cookie clients send the remember-device token as an HttpOnly cookie.
            request = new NhPasskeySignInRequest
            {
                CeremonyToken = request.CeremonyToken,
                Credential = request.Credential,
                RememberDeviceToken = HttpContext.Request.Cookies[configuration.RememberDeviceCookieName],
            };
        }

        var result = await multiFactorService.AuthenticatePasskeyAsync(
            request,
            HttpContext,
            Configuration.AuthenticateRequiredClaims);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        if (result.Data!.Session == null)
        {
            return TypedResults.Ok(NhLoginResponse.FromChallenge(result.Data.Challenge!));
        }

        WriteTokenToCookie(result.Data.Session);
        return TypedResults.Ok(NhLoginResponse.FromSession(result.Data.Session));
    }
}

/// <summary>
/// Creates passkey request options for a pending second-factor challenge.
/// </summary>
public class NhTwoFactorPasskeyOptionsHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorPasskeyOptionsHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/passkey/options", configuration)
    {
        Handler = BeginAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start a two-factor passkey assertion")
            .WithDescription(
                "Returns WebAuthn request options for the passkeys of the user behind the challenge. The ceremony " +
                "token only completes that challenge.")
            .Produces<NhPasskeyOptionsResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Start two-factor passkey assertion")]
    private async Task<IResult> BeginAsync([FromBody] NhTwoFactorChallengeRequest? request)
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

        var result = await multiFactorService.BeginTwoFactorPasskeyAsync(request!.ChallengeToken, HttpContext!);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Completes a second-factor challenge with a passkey and issues the session.
/// </summary>
public class NhTwoFactorPasskeyVerifyHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorPasskeyVerifyHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/passkey", configuration)
    {
        Handler = VerifyAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Complete a two-factor challenge with a passkey")
            .WithDescription(
                "Verifies the passkey assertion for the challenge and issues the access and refresh tokens. A failed " +
                "assertion counts towards the account lockout. With rememberDevice the response also sets a " +
                "remember-device cookie and returns its token.")
            .Produces<NhLoginResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Verify two-factor challenge with passkey")]
    private async Task<IResult> VerifyAsync([FromBody] NhTwoFactorPasskeyVerifyRequest? request)
    {
        var validation = RequireFields(
            (nameof(NhTwoFactorPasskeyVerifyRequest.ChallengeToken), request?.ChallengeToken),
            (nameof(NhTwoFactorPasskeyVerifyRequest.CeremonyToken), request?.CeremonyToken),
            (nameof(NhTwoFactorPasskeyVerifyRequest.Credential), CredentialJson(request?.Credential)));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.VerifyTwoFactorPasskeyAsync(
            request!,
            HttpContext!,
            Configuration.AuthenticateRequiredClaims);
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

    /// <summary>The credential's JSON text, or <see langword="null"/> when it is missing.</summary>
    internal static string? CredentialJson(JsonElement? credential)
    {
        return credential is { ValueKind: JsonValueKind.Object } element ? element.GetRawText() : null;
    }
}

/// <summary>
/// Creates passkey creation options during a required enrollment.
/// </summary>
public class NhTwoFactorEnrollmentPasskeyOptionsHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEnrollmentPasskeyOptionsHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/enrollment/passkey/options", configuration)
    {
        Handler = BeginAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start required passkey enrollment")
            .WithDescription("Returns WebAuthn creation options for the user behind the enrollment token.")
            .Produces<NhPasskeyOptionsResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Start required passkey enrollment")]
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

        var result = await multiFactorService.BeginEnrollmentPasskeyAsync(request!.EnrollmentToken, HttpContext!);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Registers a passkey during a required enrollment and issues the session.
/// </summary>
public class NhTwoFactorEnrollmentPasskeyHandler : NhTwoFactorStepEndpointHandler
{
    public NhTwoFactorEnrollmentPasskeyHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "authentication/two-factor/enrollment/passkey", configuration)
    {
        Handler = ConfirmAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Confirm required passkey enrollment")
            .WithDescription(
                "Verifies the authenticator's attestation, stores the passkey, enables two-factor authentication, " +
                "returns the recovery codes once and issues the session.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Authentication")]
    [EndpointName("Confirm required passkey enrollment")]
    private async Task<IResult> ConfirmAsync([FromBody] NhPasskeyEnrollmentRequest? request)
    {
        var validation = RequireFields(
            (nameof(NhPasskeyEnrollmentRequest.EnrollmentToken), request?.EnrollmentToken),
            (nameof(NhPasskeyEnrollmentRequest.CeremonyToken), request?.CeremonyToken),
            (nameof(NhPasskeyEnrollmentRequest.Credential), NhTwoFactorPasskeyVerifyHandler.CredentialJson(request?.Credential)));
        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var multiFactorService = MultiFactorService;
        if (multiFactorService == null)
        {
            return ConfigurationInvalid();
        }

        var result = await multiFactorService.ConfirmEnrollmentPasskeyAsync(request!, HttpContext!);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return EnrollmentCompleted(result.Data!);
    }
}

/// <summary>
/// Lists the signed-in user's passkeys.
/// </summary>
public class NhPasskeysEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhPasskeysEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/passkeys", HttpMethod.Get, configuration)
    {
        Handler = GetPasskeysAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("List passkeys")
            .WithDescription("Returns the registered passkeys without their public keys.")
            .Produces<IReadOnlyList<NhPasskeyViewModel>>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("List passkeys")]
    private async Task<IResult> GetPasskeysAsync()
    {
        var current = await GetCurrentUserAsync(forChange: false);
        if (current.Error != null)
        {
            return current.Error;
        }

        var passkeys = await TwoFactorService.GetPasskeysAsync(current.User!, HttpContext!.RequestAborted);
        return TypedResults.Ok(passkeys);
    }
}

/// <summary>
/// Creates WebAuthn creation options for a new passkey.
/// </summary>
public class NhPasskeyRegistrationOptionsEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhPasskeyRegistrationOptionsEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/passkeys/options", HttpMethod.Post, configuration)
    {
        Handler = BeginAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Start passkey registration")
            .WithDescription(
                "Returns WebAuthn creation options and a ceremony token. Adding a passkey while two-factor " +
                "authentication is enabled requires reauthentication.")
            .Produces<NhPasskeyOptionsResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Start passkey registration")]
    private async Task<IResult> BeginAsync([FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.BeginPasskeyRegistrationAsync(
            current.User!,
            request?.Reauthentication,
            HttpContext!,
            HttpContext!.RequestAborted);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.Ok(result.Data!);
    }
}

/// <summary>
/// Registers a passkey for the signed-in user.
/// </summary>
public class NhPasskeyRegistrationEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhPasskeyRegistrationEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/passkeys", HttpMethod.Post, configuration)
    {
        Handler = RegisterAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Register a passkey")
            .WithDescription(
                "Verifies the authenticator's attestation, stores the passkey and enables two-factor authentication. " +
                "The first second factor also returns recovery codes once. Every other session ends; the response " +
                "contains a new session for this device.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Register passkey")]
    private async Task<IResult> RegisterAsync([FromBody] NhPasskeyRegistrationRequest? request)
    {
        var credential = NhTwoFactorPasskeyVerifyHandler.CredentialJson(request?.Credential);
        var validation = new TaskResult();
        if (string.IsNullOrWhiteSpace(request?.CeremonyToken))
        {
            validation.AddError(nameof(NhPasskeyRegistrationRequest.CeremonyToken), "Field is required");
        }

        if (credential == null)
        {
            validation.AddError(nameof(NhPasskeyRegistrationRequest.Credential), "Field is required");
        }

        if (!validation.Success)
        {
            return BadRequest(validation);
        }

        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.CompletePasskeyRegistrationAsync(
            current.User!,
            request!.CeremonyToken,
            credential!,
            request.Name,
            HttpContext!,
            current.User!.Id,
            HttpContext!.RequestAborted);

        return await ChangeResponseAsync(result);
    }
}

/// <summary>
/// Renames a passkey of the signed-in user.
/// </summary>
public class NhPasskeyRenameEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhPasskeyRenameEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/passkeys/{passkeyId}", HttpMethod.Put, configuration)
    {
        Handler = RenameAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Rename a passkey")
            .WithDescription("Changes the name that helps the user recognize the passkey.")
            .Produces(StatusCodes.Status204NoContent);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Rename passkey")]
    private async Task<IResult> RenameAsync([FromRoute] string passkeyId, [FromBody] NhPasskeyRenameRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(TaskResult.Failed(nameof(NhPasskeyRenameRequest.Name), "Field is required"));
        }

        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.RenamePasskeyAsync(
            current.User!,
            passkeyId,
            request.Name,
            HttpContext!.RequestAborted);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return TypedResults.NoContent();
    }
}

/// <summary>
/// Removes a passkey of the signed-in user.
/// </summary>
public class NhPasskeyRemoveEndpointHandler<TUser> : NhTwoFactorAccountEndpointHandler<TUser>
    where TUser : IdentityUser<Guid>
{
    public NhPasskeyRemoveEndpointHandler(
        AuthenticationConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor, "account/passkeys/{passkeyId}/remove", HttpMethod.Post, configuration)
    {
        Handler = RemoveAsync;
    }

    public override void Configure(RouteHandlerBuilder builder)
    {
        base.Configure(builder);
        builder
            .WithSummary("Remove a passkey")
            .WithDescription(
                "Removes the passkey after reauthentication. Removing the last second factor disables two-factor " +
                "authentication and fails when the policy requires it. Every other session ends; the response " +
                "contains a new session for this device.")
            .Produces<NhTwoFactorChangeResponse>(StatusCodes.Status200OK);
    }

    [ApiExplorerSettings(GroupName = "Authentication")]
    [Tags("Account")]
    [EndpointName("Remove passkey")]
    private async Task<IResult> RemoveAsync(
        [FromRoute] string passkeyId,
        [FromBody] NhTwoFactorReauthenticationRequest? request)
    {
        var current = await GetCurrentUserAsync(forChange: true);
        if (current.Error != null)
        {
            return current.Error;
        }

        var result = await TwoFactorService.RemovePasskeyAsync(
            current.User!,
            passkeyId,
            request?.Reauthentication,
            current.User!.Id,
            HttpContext!.RequestAborted);

        return await ChangeResponseAsync(result);
    }
}
