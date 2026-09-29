using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Authorization;
using System.Security.Claims;
using AuthenticationService = Microsoft.AspNetCore.Authentication.AuthenticationService;

namespace SampleProjectManagement.Api.Services;

public sealed class SampleAuthenticationService : NhAuthenticationService<
    NhUser,
    NhDivision,
    NhDivisionUser,
    NhDivisionRole,
    NhDivisionUserRole,
    NhDivisionRoleClaim>
{
    public SampleAuthenticationService(
        SignInManager<NhUser> signInManager,
        INhUserManager<NhUser> userManager,
        ILogger<AuthenticationService> logger,
        IConfiguration configuration,
        TokenValidationParameters tokenValidationParameters,
        AuthenticationConfiguration authConfiguration,
        NhTwoFactorAuthenticationContext<NhUser> twoFactorContext)
        : base(
            signInManager,
            userManager,
            logger,
            configuration,
            tokenValidationParameters,
            authConfiguration,
            twoFactorContext)
    {
    }

    protected override Task<NhUser?> FindUserByUsernameAsync(string username) =>
        base.FindUserByUsernameAsync(username.Trim());

    protected override async Task<List<Claim>> GetClaimsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var claims = await base.GetClaimsAsync(userId, cancellationToken);

        // Keep volatile, potentially large authorization scopes out of the JWT.
        // The account endpoint still returns them to the frontend and
        // SampleRuntimeClaimsTransformation restores them for backend requests.
        return claims
            .Where(claim => !SampleRuntimeAuthorizationClaims.IsRequestScoped(claim))
            .ToList();
    }

    /// <summary>
    /// Shows how a consumer-specific credential creates a standard NewHeap session
    /// without changing the user's password or revoking another device's session.
    /// Users who enrolled a second factor receive a two-factor challenge instead of a
    /// session, exactly like a password sign-in.
    /// </summary>
    public async Task<TaskResult<NhAuthenticationResult>> AuthenticateCustomCredentialAsync(
        string username,
        Func<NhUser, CancellationToken, Task<bool>> verifyCredentialAsync,
        IEnumerable<Claim>? requiredClaims = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifyCredentialAsync);

        var user = await FindUserByUsernameAsync(username);
        if (user == null)
        {
            return TaskResult<NhAuthenticationResult>.Failed("Invalid credential");
        }

        if (!await verifyCredentialAsync(user, cancellationToken))
        {
            var accessFailedResult = await _userManager.AccessFailedAsync(user);
            if (!accessFailedResult.Succeeded)
            {
                return TaskResult<NhAuthenticationResult>.Failed("Could not update authentication state");
            }

            return TaskResult<NhAuthenticationResult>.Failed("Invalid credential");
        }

        return await CompleteFirstFactorAsync(user, NhAuthenticationFactors.Custom("pin"), requiredClaims);
    }
}
