using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class TwoFactorAuthenticationProviderTests
{
    private const string Credential = "Initial1!Password";
    private const string SecurityOfficerRole = "security-officer";
    private const string PasskeyDomain = "localhost";
    private const string PasskeyOrigin = "https://localhost";

    [Fact]
    public async Task TwoFactorLifecycleWorksOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyProviderAsync(options => options.UseNewHeapSqlServer(sqlServer.GetConnectionString()));
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyProviderAsync(options => options.UseNewHeapPostgreSql(postgreSql.GetConnectionString()));
        }
    }

    [Fact]
    public void ServiceConstructedWithoutTheTwoFactorContextReportsTwoFactorUnavailable()
    {
        var optionsBuilder = new DbContextOptionsBuilder<TwoFactorDbContext>()
            .UseNewHeapPostgreSql("Host=localhost;Database=unused");
        var environment = new TwoFactorEnvironment(
            optionsBuilder.Options,
            new TestTimeProvider(DateTimeOffset.UtcNow),
            new EphemeralDataProtectionProvider(),
            TwoFactorEnvironment.CreateConfiguration(null),
            new TestTwoFactorPolicy());

        using var stack = environment.CreateStack();
        var legacyService = stack.CreateLegacyAuthenticationService();

        Assert.False(((INhMultiFactorAuthenticationService)legacyService).IsTwoFactorAvailable);
        Assert.True(((INhMultiFactorAuthenticationService)stack.Authentication).IsTwoFactorAvailable);
    }

    private static async Task VerifyProviderAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TwoFactorDbContext>();
        configureProvider(optionsBuilder);

        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var environment = new TwoFactorEnvironment(
            optionsBuilder.Options,
            new TestTimeProvider(DateTimeOffset.UtcNow),
            dataProtectionProvider,
            TwoFactorEnvironment.CreateConfiguration(null),
            new TestTwoFactorPolicy());

        await using (var context = new TwoFactorDbContext(optionsBuilder.Options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        await VerifyEnrollmentChallengeAndDisableAsync(environment);
        await VerifyConcurrentVerificationCommitsOnceAsync(environment);
        await VerifyPolicyGatesEverySessionSourceAsync(environment);

        // The remaining scenarios use the default policy with a role requirement, e-mail codes,
        // remembered devices and security notifications.
        var featureConfiguration = TwoFactorEnvironment.CreateConfiguration(configuration =>
        {
            configuration.EmailCodesEnabled = true;
            configuration.SecurityNotificationsEnabled = true;
            configuration.RememberDeviceEnabled = true;
            configuration.AddRequiredRoles([SecurityOfficerRole]);
        });
        var featureEnvironment = new TwoFactorEnvironment(
            optionsBuilder.Options,
            new TestTimeProvider(DateTimeOffset.UtcNow.AddHours(1)),
            dataProtectionProvider,
            featureConfiguration,
            new NhDefaultTwoFactorPolicy(featureConfiguration));

        await VerifyRoleEnforcementAndSignInEnrollmentAsync(featureEnvironment);
        await VerifyEmailCodesAsync(featureEnvironment);
        await VerifyRememberedDevicesAsync(featureEnvironment);
        await VerifyResetAndPolicyOperationsAsync(featureEnvironment);

        var passkeyConfiguration = TwoFactorEnvironment.CreateConfiguration(configuration =>
        {
            configuration.PasskeysEnabled = true;
            configuration.PasskeyServerDomain = PasskeyDomain;
            configuration.PasskeyAllowedOrigins = [PasskeyOrigin];
            configuration.SecurityNotificationsEnabled = true;
            configuration.AddRequiredRoles([SecurityOfficerRole]);
        });
        var passkeyEnvironment = new TwoFactorEnvironment(
            optionsBuilder.Options,
            new TestTimeProvider(DateTimeOffset.UtcNow.AddHours(2)),
            dataProtectionProvider,
            passkeyConfiguration,
            new NhDefaultTwoFactorPolicy(passkeyConfiguration));

        await VerifyPasskeysAsync(passkeyEnvironment);
        await VerifyPasskeyEnrollmentAndPolicyAsync(passkeyEnvironment);
    }

    private static async Task VerifyEnrollmentChallengeAndDisableAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var user = await stack.CreateUserAsync("enrollment@example.test");
        var loginRequest = new AuthenticateRequest(user.UserName!, Credential);

        // A user without a second factor still signs in with the password alone.
        var passwordOnly = await stack.Authentication.AuthenticateAsync(loginRequest);
        Assert.True(passwordOnly.Success);
        Assert.NotNull(passwordOnly.Data!.Session);

        await stack.AddRefreshTokenAsync(user.Id, "existing-device");

        // Enrollment keeps the new key pending until it is confirmed.
        var setup = await stack.TwoFactor.BeginAuthenticatorSetupAsync(user, reauthentication: null);
        Assert.True(setup.Success);
        Assert.StartsWith("otpauth://totp/", setup.Data!.AuthenticatorUri, StringComparison.Ordinal);
        Assert.StartsWith("data:image/png;base64,", setup.Data.QrCodeDataUri, StringComparison.Ordinal);
        Assert.False(await stack.UserManager.GetTwoFactorEnabledAsync(user));

        var key = setup.Data.SharedKey.Replace(" ", string.Empty).ToUpperInvariant();

        var wrongConfirmation = await stack.TwoFactor.ConfirmAuthenticatorAsync(
            user,
            environment.CodeAtOffset(key, steps: 5),
            user.Id);
        AssertFailure(wrongConfirmation, NhTwoFactorFailureCodes.InvalidCode);
        Assert.False(await stack.UserManager.GetTwoFactorEnabledAsync(user));

        var securityStampBeforeEnrollment = await stack.UserManager.GetSecurityStampAsync(user);
        var confirmation = await stack.TwoFactor.ConfirmAuthenticatorAsync(user, environment.CurrentCode(key), user.Id);

        Assert.True(confirmation.Success);
        Assert.True(confirmation.Data!.SessionsInvalidated);
        Assert.Equal(10, confirmation.Data.RecoveryCodes!.Count);
        Assert.True(await stack.UserManager.GetTwoFactorEnabledAsync(user));
        Assert.NotEqual(securityStampBeforeEnrollment, await stack.UserManager.GetSecurityStampAsync(user));
        Assert.Equal(0, await stack.CountRefreshTokensAsync(user.Id));
        Assert.Equal(10, await stack.UserManager.CountRecoveryCodesAsync(user));

        var storedRecoveryCodes = await stack.Context.Set<IdentityUserToken<Guid>>()
            .Where(x => x.UserId == user.Id && x.Name == "RecoveryCodes")
            .Select(x => x.Value)
            .SingleAsync();
        Assert.All(storedRecoveryCodes!.Split(';'), code => Assert.StartsWith("nhv1:", code, StringComparison.Ordinal));
        Assert.DoesNotContain(confirmation.Data.RecoveryCodes[0], storedRecoveryCodes, StringComparison.Ordinal);

        // The activated key is a normal Identity authenticator key.
        Assert.True(await stack.UserManager.VerifyTwoFactorTokenAsync(
            user,
            TokenOptions.DefaultAuthenticatorProvider,
            NhTotp.ComputeCode(NhBase32.Decode(key), NhTotp.GetTimeStep(DateTimeOffset.UtcNow))));

        // The password step now returns a challenge and does not reset the failed attempts.
        await stack.UserManager.AccessFailedAsync(user);
        var login = await stack.Authentication.AuthenticateAsync(loginRequest);

        Assert.True(login.Success);
        Assert.Null(login.Data!.Session);
        var challenge = login.Data.Challenge!;
        Assert.False(challenge.RememberDeviceAvailable);
        Assert.Equal(NhAuthenticationStepStatuses.TwoFactorRequired, challenge.Status);
        Assert.Equal([NhTwoFactorMethods.Authenticator, NhTwoFactorMethods.RecoveryCode], challenge.Methods);
        Assert.Equal(0, await stack.CountRefreshTokensAsync(user.Id));
        Assert.Equal(1, await stack.UserManager.GetAccessFailedCountAsync(user));

        // Paths that can only return a complete session refuse the user instead of skipping the factor.
        AssertFailure(await stack.Authentication.Authenticate(loginRequest), NhTwoFactorFailureCodes.Required);
        AssertFailure(await stack.Authentication.CreateSessionForVerifiedCredentialAsync(user), NhTwoFactorFailureCodes.Required);
        AssertFailure(await stack.Authentication.LoginWithoutValidations(user.Id, true), NhTwoFactorFailureCodes.Required);
        Assert.Equal(0, await stack.CountRefreshTokensAsync(user.Id));

        // A consumer credential completed through the first-factor hook receives a challenge.
        var customCredential = await stack.Authentication.CompleteCustomCredentialAsync(user);
        Assert.True(customCredential.Success);
        Assert.NotNull(customCredential.Data!.Challenge);

        // A wrong code counts towards the lockout and leaves the challenge usable.
        environment.Clock.Advance(TimeSpan.FromSeconds(30));
        var wrongCode = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = environment.CodeAtOffset(key, steps: 5),
        });
        AssertFailure(wrongCode, NhTwoFactorFailureCodes.InvalidCode);
        Assert.Equal(2, await stack.UserManager.GetAccessFailedCountAsync(user));

        var currentCode = environment.CurrentCode(key);
        var verified = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = currentCode,
        });

        Assert.True(verified.Success);
        Assert.NotNull(verified.Data!.Session);
        Assert.Equal(1, await stack.CountRefreshTokensAsync(user.Id));
        Assert.Equal(0, await stack.UserManager.GetAccessFailedCountAsync(user));

        // A challenge completes once, and a used authenticator step cannot complete another challenge.
        var replayedChallenge = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = currentCode,
        });
        AssertFailure(replayedChallenge, NhTwoFactorFailureCodes.ChallengeExpired);

        var secondChallenge = (await stack.Authentication.AuthenticateAsync(loginRequest)).Data!.Challenge!;
        var replayedCode = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = secondChallenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = currentCode,
        });
        AssertFailure(replayedCode, NhTwoFactorFailureCodes.InvalidCode);

        // Recovery codes ignore case and can be redeemed once.
        var recoveryCode = confirmation.Data.RecoveryCodes[0].ToLowerInvariant();
        var recovered = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = secondChallenge.ChallengeToken,
            Method = NhTwoFactorMethods.RecoveryCode,
            Code = recoveryCode,
        });
        Assert.True(recovered.Success);
        Assert.Equal(9, await stack.UserManager.CountRecoveryCodesAsync(user));

        var thirdChallenge = (await stack.Authentication.AuthenticateAsync(loginRequest)).Data!.Challenge!;
        var reusedRecoveryCode = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = thirdChallenge.ChallengeToken,
            Method = NhTwoFactorMethods.RecoveryCode,
            Code = recoveryCode,
        });
        AssertFailure(reusedRecoveryCode, NhTwoFactorFailureCodes.InvalidCode);
        Assert.Equal(9, await stack.UserManager.CountRecoveryCodesAsync(user));

        // The failed redemption was rolled back; continue with the persisted user state.
        user = (await stack.UserManager.FindByIdAsync(user.Id.ToString()))!;
        Assert.Equal(1, await stack.UserManager.GetAccessFailedCountAsync(user));

        // Tampered, expired and stamp-invalidated challenges are rejected without counting an attempt.
        environment.Clock.Advance(TimeSpan.FromSeconds(30));
        var tampered = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = thirdChallenge.ChallengeToken + "x",
            Method = NhTwoFactorMethods.Authenticator,
            Code = environment.CurrentCode(key),
        });
        AssertFailure(tampered, NhTwoFactorFailureCodes.ChallengeExpired);

        environment.Clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = thirdChallenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = environment.CurrentCode(key),
        });
        AssertFailure(expired, NhTwoFactorFailureCodes.ChallengeExpired);

        var fourthChallenge = (await stack.Authentication.AuthenticateAsync(loginRequest)).Data!.Challenge!;
        await stack.UserManager.UpdateSecurityStampAsync(user);
        var afterStampChange = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = fourthChallenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = environment.CurrentCode(key),
        });
        AssertFailure(afterStampChange, NhTwoFactorFailureCodes.ChallengeExpired);

        // The default policy trusts the identity provider's MFA, and enrolled users keep refreshing.
        var external = await stack.Authentication.AuthenticateExternalAsync(user.Id, NhAuthenticationFactors.MicrosoftOAuth);
        Assert.True(external.Success);
        var externalSession = external.Data!.Session!;

        var refreshed = await stack.Authentication.AuthenticateRefreshTokenAsync(
            new RefreshTokenRequest(user.UserName!, externalSession.RefreshToken!));
        Assert.True(refreshed.Success);

        // Sensitive changes require reauthentication.
        var withoutReauthentication = await stack.TwoFactor.RegenerateRecoveryCodesAsync(user, null, user.Id);
        AssertFailure(withoutReauthentication, NhTwoFactorFailureCodes.ReauthenticationRequired);

        var wrongPassword = await stack.TwoFactor.RegenerateRecoveryCodesAsync(
            user,
            new NhTwoFactorReauthentication { Password = "test-wrong-password" },
            user.Id);
        AssertFailure(wrongPassword, NhTwoFactorFailureCodes.ReauthenticationFailed);

        environment.Clock.Advance(TimeSpan.FromSeconds(30));
        var regenerated = await stack.TwoFactor.RegenerateRecoveryCodesAsync(
            user,
            new NhTwoFactorReauthentication { Method = NhTwoFactorMethods.Authenticator, Code = environment.CurrentCode(key) },
            user.Id);
        Assert.True(regenerated.Success);
        Assert.Equal(10, regenerated.Data!.RecoveryCodes!.Count);
        Assert.False(regenerated.Data.SessionsInvalidated);

        // Disabling ends every session and restores password-only sign-in.
        var disabled = await stack.TwoFactor.DisableAsync(
            user,
            new NhTwoFactorReauthentication { Password = Credential },
            user.Id);

        Assert.True(disabled.Success);
        Assert.True(disabled.Data!.SessionsInvalidated);
        Assert.False(await stack.UserManager.GetTwoFactorEnabledAsync(user));
        Assert.Equal(0, await stack.UserManager.CountRecoveryCodesAsync(user));
        Assert.Equal(0, await stack.CountRefreshTokensAsync(user.Id));

        var renewed = await stack.Authentication.RenewSessionAsync(disabled.Data.RenewalProof!);
        Assert.True(renewed.Success);

        var afterDisable = await stack.Authentication.AuthenticateAsync(loginRequest);
        Assert.True(afterDisable.Success);
        Assert.NotNull(afterDisable.Data!.Session);
    }

    private static async Task VerifyConcurrentVerificationCommitsOnceAsync(TwoFactorEnvironment environment)
    {
        string key;
        string challengeToken;
        NhUser user;

        using (var enrollmentStack = environment.CreateStack())
        {
            user = await enrollmentStack.CreateUserAsync("concurrent@example.test");
            key = await enrollmentStack.EnrollAsync(user, environment);

            environment.Clock.Advance(TimeSpan.FromSeconds(30));
            var login = await enrollmentStack.Authentication.AuthenticateAsync(
                new AuthenticateRequest(user.UserName!, Credential));
            challengeToken = login.Data!.Challenge!.ChallengeToken;
        }

        var code = environment.CurrentCode(key);
        using var firstStack = environment.CreateStack();
        using var secondStack = environment.CreateStack();

        var results = await Task.WhenAll(
            firstStack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
            {
                ChallengeToken = challengeToken,
                Method = NhTwoFactorMethods.Authenticator,
                Code = code,
            }),
            secondStack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
            {
                ChallengeToken = challengeToken,
                Method = NhTwoFactorMethods.Authenticator,
                Code = code,
            }));

        Assert.Single(results, result => result.Success);

        using var verificationStack = environment.CreateStack();
        Assert.Equal(1, await verificationStack.CountRefreshTokensAsync(user.Id));
    }

    private static async Task VerifyPolicyGatesEverySessionSourceAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var user = await stack.CreateUserAsync("policy@example.test");
        environment.TestPolicy.RequiredUserIds.Add(user.Id);

        try
        {
            await stack.AddRefreshTokenAsync(user.Id, "device-before-policy");

            // A user that the policy covers enrolls before any session is issued.
            var login = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(user.UserName!, Credential));
            Assert.True(login.Success);
            Assert.Null(login.Data!.Session);
            Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, login.Data.Challenge!.Status);

            var external = await stack.Authentication.AuthenticateExternalAsync(user.Id, NhAuthenticationFactors.MicrosoftOAuth);
            Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, external.Data!.Challenge!.Status);

            AssertFailure(
                await stack.Authentication.AuthenticateRefreshTokenAsync(new RefreshTokenRequest(user.UserName!, "device-before-policy")),
                NhTwoFactorFailureCodes.EnrollmentRequired);
            AssertFailure(
                await stack.Authentication.CreateSessionForVerifiedCredentialAsync(user),
                NhTwoFactorFailureCodes.EnrollmentRequired);
            Assert.Equal(1, await stack.CountRefreshTokensAsync(user.Id));

            var status = await stack.TwoFactor.GetStatusAsync(user);
            Assert.True(status.Required);
            Assert.False(status.Enabled);

            await stack.EnrollAsync(user, environment);

            // The policy keeps the second factor on.
            var disable = await stack.TwoFactor.DisableAsync(
                user,
                new NhTwoFactorReauthentication { Password = Credential },
                user.Id);
            AssertFailure(disable, NhTwoFactorFailureCodes.RequiredByPolicy);
            Assert.True(await stack.UserManager.GetTwoFactorEnabledAsync(user));
        }
        finally
        {
            environment.TestPolicy.RequiredUserIds.Remove(user.Id);
        }
    }

    private static async Task VerifyRoleEnforcementAndSignInEnrollmentAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var officer = await stack.CreateUserAsync("officer@example.test");
        await stack.AddToRoleAsync(officer, SecurityOfficerRole);
        var regularUser = await stack.CreateUserAsync("regular@example.test");
        await stack.AddRefreshTokenAsync(officer.Id, "officer-device");

        // Users outside the required role keep signing in with the password alone.
        var regularLogin = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(regularUser.UserName!, Credential));
        Assert.NotNull(regularLogin.Data!.Session);

        // A member of the required role enrolls during sign-in before any session is issued.
        var login = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(officer.UserName!, Credential));
        Assert.True(login.Success);
        Assert.Null(login.Data!.Session);
        var enrollment = login.Data.Challenge!;
        Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, enrollment.Status);
        Assert.Equal([NhTwoFactorMethods.Authenticator], enrollment.Methods);

        AssertFailure(
            await stack.Authentication.AuthenticateRefreshTokenAsync(new RefreshTokenRequest(officer.UserName!, "officer-device")),
            NhTwoFactorFailureCodes.EnrollmentRequired);

        // An enrollment token never completes a sign-in challenge.
        AssertFailure(
            await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
            {
                ChallengeToken = enrollment.ChallengeToken,
                Method = NhTwoFactorMethods.Authenticator,
                Code = "123456",
            }),
            NhTwoFactorFailureCodes.ChallengeExpired);

        var setup = await stack.Authentication.BeginEnrollmentAuthenticatorSetupAsync(enrollment.ChallengeToken);
        Assert.True(setup.Success);
        var key = setup.Data!.SharedKey.Replace(" ", string.Empty).ToUpperInvariant();

        var completion = await stack.Authentication.ConfirmEnrollmentAuthenticatorAsync(
            enrollment.ChallengeToken,
            environment.CurrentCode(key));

        Assert.True(completion.Success);
        Assert.NotNull(completion.Data!.Session.RefreshToken);
        Assert.Equal(10, completion.Data.RecoveryCodes!.Count);
        Assert.Contains(environment.Notifications.Names, name => name == "two-factor-enabled");

        // Enrollment rotated the security stamp, so the enrollment token cannot be reused.
        AssertFailure(
            await stack.Authentication.ConfirmEnrollmentAuthenticatorAsync(enrollment.ChallengeToken, environment.CurrentCode(key)),
            NhTwoFactorFailureCodes.ChallengeExpired);

        environment.Clock.Advance(TimeSpan.FromSeconds(30));
        var secondLogin = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(officer.UserName!, Credential));
        Assert.Equal(NhAuthenticationStepStatuses.TwoFactorRequired, secondLogin.Data!.Challenge!.Status);

        var status = await stack.TwoFactor.GetStatusAsync(officer);
        Assert.True(status.Required);
        Assert.True(status.Enabled);

        AssertFailure(
            await stack.TwoFactor.DisableAsync(officer, new NhTwoFactorReauthentication { Password = Credential }, officer.Id),
            NhTwoFactorFailureCodes.RequiredByPolicy);
    }

    private static async Task VerifyEmailCodesAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var user = await stack.CreateUserAsync("email-factor@example.test");

        var setup = await stack.TwoFactor.BeginEmailSetupAsync(user, reauthentication: null);
        Assert.True(setup.Success);
        var confirmationCode = environment.Notifications.LastEmailCode();

        AssertFailure(
            await stack.TwoFactor.ConfirmEmailSetupAsync(user, confirmationCode == "000000" ? "111111" : "000000", user.Id),
            NhTwoFactorFailureCodes.InvalidCode);

        var confirmation = await stack.TwoFactor.ConfirmEmailSetupAsync(user, confirmationCode, user.Id);
        Assert.True(confirmation.Success);
        Assert.Equal(10, confirmation.Data!.RecoveryCodes!.Count);
        Assert.Contains(NhTwoFactorMethods.Email, (await stack.TwoFactor.GetEnrollmentAsync(user)).Methods);

        var login = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(user.UserName!, Credential));
        var challenge = login.Data!.Challenge!;
        Assert.Equal([NhTwoFactorMethods.Email, NhTwoFactorMethods.RecoveryCode], challenge.Methods);

        var firstSend = await stack.Authentication.SendTwoFactorEmailCodeAsync(challenge.ChallengeToken);
        Assert.True(firstSend.Success);
        var firstCode = environment.Notifications.LastEmailCode();

        // A new code can only be requested after the cooldown, and it replaces the previous one.
        AssertFailure(
            await stack.Authentication.SendTwoFactorEmailCodeAsync(challenge.ChallengeToken),
            NhTwoFactorFailureCodes.EmailCooldown);

        environment.Clock.Advance(TimeSpan.FromMinutes(2));
        var sent = await stack.Authentication.SendTwoFactorEmailCodeAsync(challenge.ChallengeToken);
        Assert.True(sent.Success);
        var signInCode = environment.Notifications.LastEmailCode();

        if (firstCode != signInCode)
        {
            AssertFailure(
                await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
                {
                    ChallengeToken = challenge.ChallengeToken,
                    Method = NhTwoFactorMethods.Email,
                    Code = firstCode,
                }),
                NhTwoFactorFailureCodes.InvalidCode);
        }

        var storedCode = await stack.Context.Set<IdentityUserToken<Guid>>()
            .Where(x => x.UserId == user.Id && x.Name == "TwoFactorEmailCode")
            .Select(x => x.Value)
            .SingleAsync();
        Assert.DoesNotContain(signInCode, storedCode!, StringComparison.Ordinal);

        var verified = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            Method = NhTwoFactorMethods.Email,
            Code = signInCode,
        });
        Assert.True(verified.Success);

        // An e-mailed code works once.
        var nextChallenge = (await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(user.UserName!, Credential))).Data!.Challenge!;
        AssertFailure(
            await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
            {
                ChallengeToken = nextChallenge.ChallengeToken,
                Method = NhTwoFactorMethods.Email,
                Code = signInCode,
            }),
            NhTwoFactorFailureCodes.InvalidCode);

        // E-mail codes do not satisfy a requirement of the role policy.
        user = (await stack.UserManager.FindByIdAsync(user.Id.ToString()))!;
        await stack.AddToRoleAsync(user, SecurityOfficerRole);
        var requiredLogin = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(user.UserName!, Credential));
        Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, requiredLogin.Data!.Challenge!.Status);
    }

    private static async Task VerifyRememberedDevicesAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var user = await stack.CreateUserAsync("remembered-device@example.test");
        var key = await stack.EnrollAsync(user, environment);
        var loginRequest = new AuthenticateRequest(user.UserName!, Credential);

        environment.Clock.Advance(TimeSpan.FromSeconds(30));
        var challenge = (await stack.Authentication.AuthenticateAsync(loginRequest)).Data!.Challenge!;
        Assert.True(challenge.RememberDeviceAvailable);
        var verified = await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = environment.CurrentCode(key),
            RememberDevice = true,
        });

        Assert.True(verified.Success);
        var rememberDeviceToken = verified.Data!.RememberDeviceToken!;

        // The remembered device skips the second factor.
        var remembered = await stack.Authentication.AuthenticateAsync(loginRequest with { RememberDeviceToken = rememberDeviceToken });
        Assert.NotNull(remembered.Data!.Session);

        // The token belongs to one user only.
        var otherUser = await stack.CreateUserAsync("other-device@example.test");
        await stack.EnrollAsync(otherUser, environment);
        var otherLogin = await stack.Authentication.AuthenticateAsync(
            new AuthenticateRequest(otherUser.UserName!, Credential) { RememberDeviceToken = rememberDeviceToken });
        Assert.NotNull(otherLogin.Data!.Challenge);

        // Forgetting devices invalidates every remember-device token.
        var forgotten = await stack.TwoFactor.ForgetDevicesAsync(
            user,
            new NhTwoFactorReauthentication { Password = Credential },
            user.Id);
        Assert.True(forgotten.Success);
        Assert.True(forgotten.Data!.SessionsInvalidated);

        var afterForget = await stack.Authentication.AuthenticateAsync(loginRequest with { RememberDeviceToken = rememberDeviceToken });
        Assert.NotNull(afterForget.Data!.Challenge);
    }

    private static async Task VerifyResetAndPolicyOperationsAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var administrator = await stack.CreateUserAsync("two-factor-administrator@example.test");
        var mustEnroll = await stack.CreateUserAsync("must-enroll@example.test");
        await stack.AddToRoleAsync(mustEnroll, SecurityOfficerRole);
        var enrolledOfficer = await stack.CreateUserAsync("enrolled-officer@example.test");
        await stack.AddToRoleAsync(enrolledOfficer, SecurityOfficerRole);
        await stack.EnrollAsync(enrolledOfficer, environment);

        await stack.AddRefreshTokenAsync(mustEnroll.Id, "must-enroll-device");
        await stack.AddRefreshTokenAsync(enrolledOfficer.Id, "enrolled-officer-device");

        var context = Substitute.For<INhBackgroundOperationContext>();
        context.Progress.Returns(Substitute.For<INhBackgroundOperationProgressContext>());

        // Reminders reach only the users whom the role requires to enroll.
        environment.Notifications.Clear();
        var reminders = await new NhTwoFactorEnrollmentReminderOperation<NhUser>(stack.TwoFactor, stack.UserManager, stack.UserManager)
            .ExecuteAsync(new NhTwoFactorEnrollmentReminderRequest(administrator.Id), context, CancellationToken.None);

        Assert.True(reminders.Success);
        var remindedUsers = environment.Notifications.InAppRecipients("two-factor-enrollment-reminder");
        Assert.Contains(mustEnroll.Id, remindedUsers);
        Assert.DoesNotContain(enrolledOfficer.Id, remindedUsers);
        Assert.DoesNotContain(administrator.Id, remindedUsers);

        // Ending sessions only affects users who must enroll.
        var revocation = await new NhTwoFactorSessionRevocationOperation<NhUser>(stack.TwoFactor, stack.UserManager, stack.UserManager)
            .ExecuteAsync(new NhTwoFactorSessionRevocationRequest(administrator.Id), context, CancellationToken.None);

        Assert.True(revocation.Success);
        Assert.Equal(0, await stack.CountRefreshTokensAsync(mustEnroll.Id));
        Assert.Equal(1, await stack.CountRefreshTokensAsync(enrolledOfficer.Id));
        await context.Progress.Received().ReportAsync(
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string?>(),
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>());

        // An administrator reset removes every factor; the role requires enrollment again.
        enrolledOfficer = (await stack.UserManager.FindByIdAsync(enrolledOfficer.Id.ToString()))!;
        var reset = await stack.TwoFactor.ResetAsync(enrolledOfficer, administrator.Id);

        Assert.True(reset.Success);
        Assert.False((await stack.TwoFactor.GetEnrollmentAsync(enrolledOfficer)).IsEnrolled);
        Assert.Equal(0, await stack.CountRefreshTokensAsync(enrolledOfficer.Id));
        Assert.Contains(environment.Notifications.Names, name => name == "two-factor-reset-by-administrator");

        var afterReset = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(enrolledOfficer.UserName!, Credential));
        Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, afterReset.Data!.Challenge!.Status);
    }

    private static async Task VerifyPasskeysAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var httpContext = CreatePasskeyHttpContext();
        var user = await stack.CreateUserAsync("passkey-user@example.test");
        await stack.AddRefreshTokenAsync(user.Id, "passkey-user-device");
        using var authenticator = new SoftwarePasskeyAuthenticator(PasskeyDomain, PasskeyOrigin);

        // Registration verifies the attestation, enables two-factor authentication and ends
        // every other session.
        var registrationOptions = await stack.TwoFactor.BeginPasskeyRegistrationAsync(user, reauthentication: null, httpContext);
        Assert.True(registrationOptions.Success);
        var credential = authenticator.CreateCredential(registrationOptions.Data!.Options);

        var registration = await stack.TwoFactor.CompletePasskeyRegistrationAsync(
            user,
            registrationOptions.Data.CeremonyToken,
            credential,
            "  Laptop  ",
            httpContext,
            user.Id);

        Assert.True(registration.Success, string.Join("; ", registration.AllErrorMessages));
        Assert.Equal(10, registration.Data!.RecoveryCodes!.Count);
        Assert.True(registration.Data.SessionsInvalidated);
        Assert.Equal(0, await stack.CountRefreshTokensAsync(user.Id));
        Assert.Contains(environment.Notifications.Names, name => name == "two-factor-enabled");

        // The registration rotated the security stamp, so the ceremony token works once.
        AssertFailure(
            await stack.TwoFactor.CompletePasskeyRegistrationAsync(
                user,
                registrationOptions.Data.CeremonyToken,
                credential,
                null,
                httpContext,
                user.Id),
            NhTwoFactorFailureCodes.ChallengeExpired);

        var passkey = Assert.Single(await stack.TwoFactor.GetPasskeysAsync(user));
        Assert.Equal("Laptop", passkey.Name);
        Assert.Equal(authenticator.CredentialIdText, passkey.Id);
        Assert.Equal(
            [NhTwoFactorMethods.Passkey, NhTwoFactorMethods.RecoveryCode],
            (await stack.TwoFactor.GetEnrollmentAsync(user)).Methods);

        // After the password, the passkey completes the challenge; a code cannot stand in for it.
        var challenge = (await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(user.UserName!, Credential))).Data!.Challenge!;
        Assert.Equal([NhTwoFactorMethods.Passkey, NhTwoFactorMethods.RecoveryCode], challenge.Methods);
        AssertFailure(
            await stack.Authentication.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
            {
                ChallengeToken = challenge.ChallengeToken,
                Method = NhTwoFactorMethods.Passkey,
                Code = "{}",
            }),
            NhTwoFactorFailureCodes.MethodNotAllowed);

        var assertionOptions = await stack.Authentication.BeginTwoFactorPasskeyAsync(challenge.ChallengeToken, httpContext);
        Assert.True(assertionOptions.Success);
        var verifyRequest = new NhTwoFactorPasskeyVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            CeremonyToken = assertionOptions.Data!.CeremonyToken,
            Credential = ParseCredential(authenticator.CreateAssertion(assertionOptions.Data.Options)),
        };

        var verified = await stack.Authentication.VerifyTwoFactorPasskeyAsync(verifyRequest, httpContext);
        Assert.True(verified.Success, string.Join("; ", verified.AllErrorMessages));
        Assert.NotNull(verified.Data!.Session!.RefreshToken);

        AssertFailure(
            await stack.Authentication.VerifyTwoFactorPasskeyAsync(verifyRequest, httpContext),
            NhTwoFactorFailureCodes.ChallengeExpired);

        // A passwordless sign-in names no user up front and satisfies the requirement itself.
        var signInOptions = await stack.Authentication.BeginPasskeySignInAsync(httpContext);
        Assert.True(signInOptions.Success);
        Assert.False(
            signInOptions.Data!.Options.TryGetProperty("allowCredentials", out var allowCredentials)
            && allowCredentials.GetArrayLength() > 0);

        var signedIn = await stack.Authentication.AuthenticatePasskeyAsync(
            new NhPasskeySignInRequest
            {
                CeremonyToken = signInOptions.Data.CeremonyToken,
                Credential = ParseCredential(authenticator.CreateAssertion(signInOptions.Data.Options)),
            },
            httpContext);
        Assert.True(signedIn.Success, string.Join("; ", signedIn.AllErrorMessages));
        Assert.NotNull(signedIn.Data!.Session!.RefreshToken);

        // A sign-in ceremony works once, even with a new signature.
        AssertFailure(
            await stack.Authentication.AuthenticatePasskeyAsync(
                new NhPasskeySignInRequest
                {
                    CeremonyToken = signInOptions.Data.CeremonyToken,
                    Credential = ParseCredential(authenticator.CreateAssertion(signInOptions.Data.Options)),
                },
                httpContext),
            NhTwoFactorFailureCodes.ChallengeExpired);

        // Assertions made for another origin are refused.
        var otherOriginOptions = (await stack.Authentication.BeginPasskeySignInAsync(httpContext)).Data!;
        AssertFailure(
            await stack.Authentication.AuthenticatePasskeyAsync(
                new NhPasskeySignInRequest
                {
                    CeremonyToken = otherOriginOptions.CeremonyToken,
                    Credential = ParseCredential(authenticator.CreateAssertion(otherOriginOptions.Options, "https://other.example.test")),
                },
                httpContext),
            NhTwoFactorFailureCodes.PasskeyInvalid);

        user = (await stack.UserManager.FindByIdAsync(user.Id.ToString()))!;
        Assert.True((await stack.TwoFactor.RenamePasskeyAsync(user, passkey.Id, "Work laptop")).Success);
        Assert.Equal("Work laptop", Assert.Single(await stack.TwoFactor.GetPasskeysAsync(user)).Name);
        AssertFailure(
            await stack.TwoFactor.RenamePasskeyAsync(user, "not-a-passkey", "Unknown"),
            NhTwoFactorFailureCodes.PasskeyNotFound);

        // Removing the only second factor needs reauthentication and disables two-factor
        // authentication, including the recovery codes.
        AssertFailure(
            await stack.TwoFactor.RemovePasskeyAsync(user, passkey.Id, reauthentication: null, user.Id),
            NhTwoFactorFailureCodes.ReauthenticationRequired);

        var removal = await stack.TwoFactor.RemovePasskeyAsync(
            user,
            passkey.Id,
            new NhTwoFactorReauthentication { Password = Credential },
            user.Id);
        Assert.True(removal.Success, string.Join("; ", removal.AllErrorMessages));
        Assert.Empty(await stack.TwoFactor.GetPasskeysAsync(user));

        var afterRemoval = await stack.TwoFactor.GetEnrollmentAsync(user);
        Assert.False(afterRemoval.IsEnrolled);
        Assert.Equal(0, afterRemoval.RecoveryCodesLeft);

        // A later passkey enables two-factor authentication without resurrecting an authenticator.
        using var secondAuthenticator = new SoftwarePasskeyAuthenticator(PasskeyDomain, PasskeyOrigin);
        var secondOptions = (await stack.TwoFactor.BeginPasskeyRegistrationAsync(user, reauthentication: null, httpContext)).Data!;
        var secondRegistration = await stack.TwoFactor.CompletePasskeyRegistrationAsync(
            user,
            secondOptions.CeremonyToken,
            secondAuthenticator.CreateCredential(secondOptions.Options),
            null,
            httpContext,
            user.Id);
        Assert.True(secondRegistration.Success, string.Join("; ", secondRegistration.AllErrorMessages));
        Assert.Equal(
            [NhTwoFactorMethods.Passkey, NhTwoFactorMethods.RecoveryCode],
            (await stack.TwoFactor.GetEnrollmentAsync(user)).Methods);
        Assert.Equal("Passkey", Assert.Single(await stack.TwoFactor.GetPasskeysAsync(user)).Name);
    }

    private static async Task VerifyPasskeyEnrollmentAndPolicyAsync(TwoFactorEnvironment environment)
    {
        using var stack = environment.CreateStack();
        var httpContext = CreatePasskeyHttpContext();
        var officer = await stack.CreateUserAsync("passkey-officer@example.test");
        await stack.AddToRoleAsync(officer, SecurityOfficerRole);
        using var authenticator = new SoftwarePasskeyAuthenticator(PasskeyDomain, PasskeyOrigin);

        // A member of the required role enrolls a passkey during sign-in.
        var enrollment = (await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(officer.UserName!, Credential))).Data!.Challenge!;
        Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, enrollment.Status);
        Assert.Equal([NhTwoFactorMethods.Authenticator, NhTwoFactorMethods.Passkey], enrollment.Methods);

        var enrollmentOptions = await stack.Authentication.BeginEnrollmentPasskeyAsync(enrollment.ChallengeToken, httpContext);
        Assert.True(enrollmentOptions.Success);

        var completion = await stack.Authentication.ConfirmEnrollmentPasskeyAsync(
            new NhPasskeyEnrollmentRequest
            {
                EnrollmentToken = enrollment.ChallengeToken,
                CeremonyToken = enrollmentOptions.Data!.CeremonyToken,
                Credential = ParseCredential(authenticator.CreateCredential(enrollmentOptions.Data.Options)),
                Name = "Security key",
            },
            httpContext);
        Assert.True(completion.Success, string.Join("; ", completion.AllErrorMessages));
        Assert.NotNull(completion.Data!.Session.RefreshToken);
        Assert.Equal(10, completion.Data.RecoveryCodes!.Count);

        // A passkey sign-in of a required user needs no other factor.
        var signInOptions = (await stack.Authentication.BeginPasskeySignInAsync(httpContext)).Data!;
        var signedIn = await stack.Authentication.AuthenticatePasskeyAsync(
            new NhPasskeySignInRequest
            {
                CeremonyToken = signInOptions.CeremonyToken,
                Credential = ParseCredential(authenticator.CreateAssertion(signInOptions.Options)),
            },
            httpContext);
        Assert.NotNull(signedIn.Data!.Session);

        // The policy keeps the last passkey of a required user.
        officer = (await stack.UserManager.FindByIdAsync(officer.Id.ToString()))!;
        var officerPasskey = Assert.Single(await stack.TwoFactor.GetPasskeysAsync(officer));
        Assert.Equal("Security key", officerPasskey.Name);
        AssertFailure(
            await stack.TwoFactor.RemovePasskeyAsync(
                officer,
                officerPasskey.Id,
                new NhTwoFactorReauthentication { Password = Credential },
                officer.Id),
            NhTwoFactorFailureCodes.RequiredByPolicy);

        // An administrator reset removes the passkeys as well.
        Assert.True((await stack.TwoFactor.ResetAsync(officer, officer.Id)).Success);
        Assert.Empty(await stack.TwoFactor.GetPasskeysAsync(officer));

        var afterReset = await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(officer.UserName!, Credential));
        Assert.Equal(NhAuthenticationStepStatuses.EnrollmentRequired, afterReset.Data!.Challenge!.Status);
    }

    private static HttpContext CreatePasskeyHttpContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString(PasskeyDomain);
        return httpContext;
    }

    private static JsonElement ParseCredential(string credentialJson)
    {
        return JsonSerializer.Deserialize<JsonElement>(credentialJson);
    }

    private static void AssertFailure(TaskResult result, string failureCode)
    {
        Assert.False(result.Success);
        Assert.Contains(result.GetResultItems(), item => item.Name == failureCode);
    }

    private sealed class TwoFactorEnvironment
    {
        private readonly DbContextOptions<TwoFactorDbContext> _options;
        private readonly IDataProtectionProvider _dataProtectionProvider;

        internal TwoFactorEnvironment(
            DbContextOptions<TwoFactorDbContext> options,
            TestTimeProvider clock,
            IDataProtectionProvider dataProtectionProvider,
            NhTwoFactorConfiguration configuration,
            INhTwoFactorPolicy policy)
        {
            _options = options;
            Clock = clock;
            _dataProtectionProvider = dataProtectionProvider;
            Configuration = configuration;
            Policy = policy;
        }

        internal TestTimeProvider Clock { get; }
        internal INhTwoFactorPolicy Policy { get; }
        internal TestTwoFactorPolicy TestPolicy => (TestTwoFactorPolicy)Policy;
        internal NhTwoFactorConfiguration Configuration { get; }
        internal NotificationRecorder Notifications { get; } = new();

        internal static NhTwoFactorConfiguration CreateConfiguration(Action<NhTwoFactorConfiguration>? configure)
        {
            var configuration = new NhTwoFactorConfiguration
            {
                Enabled = true,
                AuthenticatorEnabled = true,
                AuthenticatorIssuer = "NewHeap two-factor tests",
                RecoveryCodesEnabled = true,
                RecoveryCodeCount = 10,
            };

            configure?.Invoke(configuration);
            configuration.Validate();
            return configuration;
        }

        internal TwoFactorStack CreateStack()
        {
            return new TwoFactorStack(this, new TwoFactorDbContext(_options), _dataProtectionProvider);
        }

        internal string CurrentCode(string base32Key)
        {
            return NhTotp.ComputeCode(NhBase32.Decode(base32Key), NhTotp.GetTimeStep(Clock.GetUtcNow()));
        }

        internal string CodeAtOffset(string base32Key, int steps)
        {
            return NhTotp.ComputeCode(NhBase32.Decode(base32Key), NhTotp.GetTimeStep(Clock.GetUtcNow()) + steps);
        }
    }

    private sealed class TwoFactorStack : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private readonly RoleManager<NhUserRole> _roleManager;
        private readonly IOptions<IdentityOptions> _identityOptions;
        private readonly TwoFactorEnvironment _environment;

        internal TwoFactorStack(
            TwoFactorEnvironment environment,
            TwoFactorDbContext context,
            IDataProtectionProvider dataProtectionProvider)
        {
            _environment = environment;
            Context = context;
            _serviceProvider = new ServiceCollection().BuildServiceProvider();
            _identityOptions = Options.Create(new IdentityOptions());

            var repository = new Repository<NhUser>(context, _serviceProvider);
            var normalizer = new UpperInvariantLookupNormalizer();
            var errors = new IdentityErrorDescriber();
            var userStore = new UserStore<NhUser, NhUserRole, TwoFactorDbContext, Guid>(context);

            _roleManager = new RoleManager<NhUserRole>(
                new RoleStore<NhUserRole, TwoFactorDbContext, Guid>(context),
                [],
                normalizer,
                errors,
                Substitute.For<ILogger<RoleManager<NhUserRole>>>());

            UserManager = new NhUserManager(
                Substitute.For<IWebHostEnvironment>(),
                userStore,
                _identityOptions,
                new PasswordHasher<NhUser>(),
                [new UserValidator<NhUser>()],
                [new PasswordValidator<NhUser>()],
                normalizer,
                errors,
                _serviceProvider,
                Substitute.For<ILogger<UserManager<NhUser>>>(),
                Options.Create(new MicrosoftAuthSettings()),
                repository,
                _roleManager,
                new ValidationService(_serviceProvider),
                Substitute.For<INhDbLogService>());
            UserManager.RegisterTokenProvider(
                TokenOptions.DefaultAuthenticatorProvider,
                new AuthenticatorTokenProvider<NhUser>());

            var hostEnvironment = Substitute.For<IHostEnvironment>();
            hostEnvironment.ApplicationName.Returns("NewHeap.Platform.AspNet.Common.Tests");

            PasskeyHandler<NhUser>? passkeyHandler = null;
            if (environment.Configuration.PasskeysEnabled)
            {
                var passkeyOptions = new IdentityPasskeyOptions();
                environment.Configuration.ConfigurePasskeyOptions(passkeyOptions);
                passkeyHandler = new PasskeyHandler<NhUser>(UserManager, Options.Create(passkeyOptions));
            }

            TwoFactor = new NhTwoFactorService<NhUser>(
                UserManager,
                UserManager,
                userStore,
                Substitute.For<INhDbLogService>(),
                environment.Configuration,
                environment.Policy,
                new NhTwoFactorTicketProtector(dataProtectionProvider, environment.Clock),
                new NhQrCodeRenderer(),
                hostEnvironment,
                environment.Clock,
                new NhTwoFactorMessageComposer(new TestLocalizer(), environment.Clock),
                environment.Notifications,
                NullLogger<NhTwoFactorService<NhUser>>.Instance,
                passkeyHandler);

            Authentication = CreateAuthenticationService(
                new NhTwoFactorAuthenticationContext<NhUser>(environment.Configuration, TwoFactor));
        }

        internal TwoFactorDbContext Context { get; }
        internal NhUserManager UserManager { get; }
        internal NhTwoFactorService<NhUser> TwoFactor { get; }
        internal TestTwoFactorAuthenticationService Authentication { get; }

        internal INhAuthenticationService CreateLegacyAuthenticationService()
        {
            return new LegacyAuthenticationService(
                CreateSignInManager(),
                UserManager,
                Substitute.For<ILogger<AuthenticationService>>(),
                CreateConfiguration(),
                new TokenValidationParameters(),
                new AuthenticationConfiguration());
        }

        internal async Task<NhUser> CreateUserAsync(string email)
        {
            var user = new NhUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
            };

            var createResult = await UserManager.CreateAsync(user, Credential);
            Assert.True(createResult.Succeeded);
            return user;
        }

        internal async Task AddToRoleAsync(NhUser user, string role)
        {
            if (!await _roleManager.RoleExistsAsync(role))
            {
                Assert.True((await _roleManager.CreateAsync(new NhUserRole(role))).Succeeded);
            }

            Assert.True((await UserManager.AddToRoleAsync(user, role)).Succeeded);
        }

        internal async Task<string> EnrollAsync(NhUser user, TwoFactorEnvironment environment)
        {
            var setup = await TwoFactor.BeginAuthenticatorSetupAsync(user, reauthentication: null);
            Assert.True(setup.Success);

            var key = setup.Data!.SharedKey.Replace(" ", string.Empty).ToUpperInvariant();
            var confirmation = await TwoFactor.ConfirmAuthenticatorAsync(user, environment.CurrentCode(key), user.Id);
            Assert.True(confirmation.Success);

            return key;
        }

        internal async Task AddRefreshTokenAsync(Guid userId, string token)
        {
            Context.Set<NhUserAuthRefreshToken>().Add(new NhUserAuthRefreshToken
            {
                UserId = userId,
                Token = token,
                ExpiryDateTime = DateTimeOffset.UtcNow.AddDays(1),
            });

            await Context.SaveChangesAsync();
        }

        internal Task<int> CountRefreshTokensAsync(Guid userId)
        {
            return Context.Set<NhUserAuthRefreshToken>().CountAsync(x => x.UserId == userId);
        }

        public void Dispose()
        {
            UserManager.Dispose();
            _roleManager.Dispose();
            Context.Dispose();
            _serviceProvider.Dispose();
        }

        private TestTwoFactorAuthenticationService CreateAuthenticationService(
            NhTwoFactorAuthenticationContext<NhUser> twoFactorContext)
        {
            return new TestTwoFactorAuthenticationService(
                CreateSignInManager(),
                UserManager,
                Substitute.For<ILogger<AuthenticationService>>(),
                CreateConfiguration(),
                new TokenValidationParameters(),
                new AuthenticationConfiguration(),
                twoFactorContext);
        }

        private SignInManager<NhUser> CreateSignInManager()
        {
            return new SignInManager<NhUser>(
                UserManager,
                Substitute.For<IHttpContextAccessor>(),
                new UserClaimsPrincipalFactory<NhUser>(UserManager, _identityOptions),
                _identityOptions,
                Substitute.For<ILogger<SignInManager<NhUser>>>(),
                Substitute.For<IAuthenticationSchemeProvider>(),
                Substitute.For<IUserConfirmation<NhUser>>());
        }

        private static IConfiguration CreateConfiguration()
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://auth.example.test",
                    ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "test-signing-key-with-at-least-thirty-two-bytes",
                })
                .Build();
        }
    }

    private sealed class TestTwoFactorAuthenticationService : NhAuthenticationService<
        NhUser,
        NhDivision,
        NhDivisionUser,
        NhDivisionRole,
        NhDivisionUserRole,
        NhDivisionRoleClaim>
    {
        internal TestTwoFactorAuthenticationService(
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

        internal Task<TaskResult<UserToken>> CreateSessionForVerifiedCredentialAsync(NhUser user)
        {
            return CreateAuthenticationSessionAsync(user);
        }

        internal Task<TaskResult<NhAuthenticationResult>> CompleteCustomCredentialAsync(NhUser user)
        {
            return CompleteFirstFactorAsync(user, NhAuthenticationFactors.Custom("pin"));
        }
    }

    private sealed class LegacyAuthenticationService : NhAuthenticationService<
        NhUser,
        NhDivision,
        NhDivisionUser,
        NhDivisionRole,
        NhDivisionUserRole,
        NhDivisionRoleClaim>
    {
        internal LegacyAuthenticationService(
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
    }

    /// <summary>
    /// Requires a second factor for selected users and otherwise follows the default policy.
    /// </summary>
    private sealed class TestTwoFactorPolicy : NhDefaultTwoFactorPolicy
    {
        internal HashSet<Guid> RequiredUserIds { get; } = [];

        public override Task<NhTwoFactorRequirement> EvaluateAsync(
            NhTwoFactorPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            if (RequiredUserIds.Contains(context.UserId))
            {
                return Task.FromResult(NhTwoFactorRequirement.RequiredWith(context.EnrolledMethods, enforcedByPolicy: true));
            }

            return base.EvaluateAsync(context, cancellationToken);
        }
    }

    /// <summary>
    /// Records created notifications instead of dispatching them.
    /// </summary>
    private sealed class NotificationRecorder : INhNotificationService
    {
        private readonly List<NhNotification> _notifications = [];
        private readonly object _gate = new();

        internal IReadOnlyList<string> Names
        {
            get
            {
                lock (_gate)
                {
                    return _notifications.Select(x => x.Name).ToList();
                }
            }
        }

        public Task<TaskResult<NhNotification>> CreateAsync(NhNotification notification, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _notifications.Add(notification);
            }

            return Task.FromResult(TaskResult<NhNotification>.Succeeded(notification));
        }

        internal void Clear()
        {
            lock (_gate)
            {
                _notifications.Clear();
            }
        }

        internal string LastEmailCode()
        {
            lock (_gate)
            {
                var body = _notifications
                    .Where(x => x.Name == "two-factor-email-code")
                    .SelectMany(x => x.Deliveries)
                    .Select(x => x.Data)
                    .OfType<NhEmailDeliveryData>()
                    .Last()
                    .Body!;

                return Regex.Match(body, "\\d{6}").Value;
            }
        }

        internal IReadOnlyList<Guid> InAppRecipients(string notificationName)
        {
            lock (_gate)
            {
                return _notifications
                    .Where(x => x.Name == notificationName)
                    .SelectMany(x => x.Deliveries)
                    .Select(x => x.Data)
                    .OfType<NhUserNotificationDeliveryData>()
                    .Select(x => x.Notification.UserId!.Value)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// Returns the English source texts, formatted with their arguments.
    /// </summary>
    private sealed class TestLocalizer : IStringLocalizer<NhTwoFactorMessages>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            return [];
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        internal TestTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        internal void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
        }
    }

    private sealed class TwoFactorDbContext(DbContextOptions<TwoFactorDbContext> options)
        : NhIdentityDbContext(options)
    {
        protected override bool IncludeIdentityPasskeys => true;
    }
}
