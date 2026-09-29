using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services;

/// <summary>
/// Authentication steps that may require a second factor. <see cref="NhAuthenticationService{TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim}"/>
/// implements this interface; the two-factor endpoints use it.
/// </summary>
public interface INhMultiFactorAuthenticationService
{
    /// <summary>
    /// Whether two-factor authentication is enabled and this service was constructed with
    /// the two-factor context. Startup fails when it is enabled but unavailable.
    /// </summary>
    bool IsTwoFactorAvailable { get; }

    /// <summary>
    /// Verifies the username and password and returns a session or a second-factor challenge.
    /// </summary>
    Task<TaskResult<NhAuthenticationResult>> AuthenticateAsync(
        AuthenticateRequest request,
        IEnumerable<Claim>? requiredClaims = null);

    /// <summary>
    /// Completes a second-factor challenge and returns the session.
    /// </summary>
    Task<TaskResult<NhAuthenticationResult>> VerifyTwoFactorAsync(
        NhTwoFactorVerifyRequest request,
        IEnumerable<Claim>? requiredClaims = null);

    /// <summary>
    /// E-mails a sign-in code for a pending challenge when the user enrolled the e-mail factor.
    /// </summary>
    Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendTwoFactorEmailCodeAsync(string challengeToken);

    /// <summary>
    /// Starts authenticator enrollment for a user whom the policy requires to enroll during
    /// sign-in.
    /// </summary>
    Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginEnrollmentAuthenticatorSetupAsync(string enrollmentToken);

    /// <summary>
    /// Confirms the authenticator during a required enrollment and issues the session.
    /// </summary>
    Task<TaskResult<NhTwoFactorEnrollmentCompletion>> ConfirmEnrollmentAuthenticatorAsync(string enrollmentToken, string code);

    /// <summary>
    /// E-mails a confirmation code during a required enrollment when e-mail codes satisfy the
    /// requirement.
    /// </summary>
    Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendEnrollmentEmailCodeAsync(string enrollmentToken);

    /// <summary>
    /// Confirms the e-mail factor during a required enrollment and issues the session.
    /// </summary>
    Task<TaskResult<NhTwoFactorEnrollmentCompletion>> ConfirmEnrollmentEmailAsync(string enrollmentToken, string code);

    /// <summary>
    /// Creates passkey creation options during a required enrollment.
    /// </summary>
    Task<TaskResult<NhPasskeyOptionsResponse>> BeginEnrollmentPasskeyAsync(string enrollmentToken, HttpContext httpContext);

    /// <summary>
    /// Registers a passkey during a required enrollment and issues the session.
    /// </summary>
    Task<TaskResult<NhTwoFactorEnrollmentCompletion>> ConfirmEnrollmentPasskeyAsync(
        NhPasskeyEnrollmentRequest request,
        HttpContext httpContext);

    /// <summary>
    /// Creates passkey request options for a pending second-factor challenge.
    /// </summary>
    Task<TaskResult<NhPasskeyOptionsResponse>> BeginTwoFactorPasskeyAsync(string challengeToken, HttpContext httpContext);

    /// <summary>
    /// Completes a second-factor challenge with a passkey and returns the session.
    /// </summary>
    Task<TaskResult<NhAuthenticationResult>> VerifyTwoFactorPasskeyAsync(
        NhTwoFactorPasskeyVerifyRequest request,
        HttpContext httpContext,
        IEnumerable<Claim>? requiredClaims = null);

    /// <summary>
    /// Creates request options for a passwordless sign-in with a discoverable passkey.
    /// </summary>
    Task<TaskResult<NhPasskeyOptionsResponse>> BeginPasskeySignInAsync(HttpContext httpContext);

    /// <summary>
    /// Signs in with a passkey and returns a session, or a pending step when the two-factor
    /// policy asks for more.
    /// </summary>
    Task<TaskResult<NhAuthenticationResult>> AuthenticatePasskeyAsync(
        NhPasskeySignInRequest request,
        HttpContext httpContext,
        IEnumerable<Claim>? requiredClaims = null);

    /// <summary>
    /// Completes a sign-in that an external identity provider verified and returns a session
    /// or a second-factor challenge, depending on the two-factor policy.
    /// </summary>
    Task<TaskResult<NhAuthenticationResult>> AuthenticateExternalAsync(Guid userId, string factor);

    /// <summary>
    /// Issues a new session for the device that made a change which ended every session.
    /// </summary>
    Task<TaskResult<UserToken>> RenewSessionAsync(NhAuthenticationProof proof);
}
