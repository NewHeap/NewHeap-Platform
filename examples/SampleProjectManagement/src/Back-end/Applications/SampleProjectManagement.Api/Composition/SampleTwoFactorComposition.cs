using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Two-factor composition of the sample. <c>Program.cs</c> and the sample tests share it, so
/// the tests prove the configuration the API runs with.
/// </summary>
public static class SampleTwoFactorComposition
{
    /// <summary>
    /// Enables every second factor. Users who enroll one get a challenge after their password,
    /// and every session source (password, PIN, OAuth, passkey, refresh) respects the policy.
    /// </summary>
    public static NhTwoFactorBuilder ConfigureSampleTwoFactor(
        this NhTwoFactorBuilder twoFactor,
        IConfiguration configuration)
    {
        return twoFactor
            .EnableAuthenticator(authenticator => authenticator.Issuer = "Sample Project Management")
            .EnableRecoveryCodes()
            // E-mail codes and security notifications need WithNotifications(...).
            .EnableEmailCodes()
            .UseSecurityNotifications()
            .UseMessageComposer<SampleTwoFactorMessageComposer>()
            .EnableRememberDevice(TimeSpan.FromDays(30))
            // Passkeys use the AspNetUserPasskeys table that SampleProjectManagementDbContext
            // opts into. WebAuthn needs a domain, so open the apps on localhost, not 127.0.0.1.
            .EnablePasskeys(passkeys =>
            {
                passkeys.ServerDomain = configuration["TwoFactor:PasskeyServerDomain"];
                passkeys.AllowedOrigins.AddRange(
                    configuration.GetSection("TwoFactor:PasskeyAllowedOrigins").Get<string[]>() ?? []);
            })
            // Security officers must use a second factor; they enroll at their next sign-in.
            .RequireFor(requirement => requirement.Roles(SampleAuthorizationDefaults.SecurityOfficerRole))
            .UseAdministrationPolicy(SampleAuthorizationPolicies.TwoFactorAdministration);
    }
}
