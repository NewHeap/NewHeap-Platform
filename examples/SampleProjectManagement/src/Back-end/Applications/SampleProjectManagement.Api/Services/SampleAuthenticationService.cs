using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
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
        AuthenticationConfiguration authConfiguration)
        : base(
            signInManager,
            userManager,
            logger,
            configuration,
            tokenValidationParameters,
            authConfiguration)
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
}
