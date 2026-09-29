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
    /// Completes a sign-in that an external identity provider verified and returns a session
    /// or a second-factor challenge, depending on the two-factor policy.
    /// </summary>
    Task<TaskResult<NhAuthenticationResult>> AuthenticateExternalAsync(Guid userId, string factor);

    /// <summary>
    /// Issues a new session for the device that made a change which ended every session.
    /// </summary>
    Task<TaskResult<UserToken>> RenewSessionAsync(NhAuthenticationProof proof);
}
