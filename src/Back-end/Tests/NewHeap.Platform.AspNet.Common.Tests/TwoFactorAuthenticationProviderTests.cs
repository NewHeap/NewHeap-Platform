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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class TwoFactorAuthenticationProviderTests
{
    private const string Credential = "Initial1!Password";

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

        var environment = new TwoFactorEnvironment(
            optionsBuilder.Options,
            new TestTimeProvider(DateTimeOffset.UtcNow),
            new EphemeralDataProtectionProvider(),
            new TestTwoFactorPolicy());

        await using (var context = new TwoFactorDbContext(optionsBuilder.Options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        await VerifyEnrollmentChallengeAndDisableAsync(environment);
        await VerifyConcurrentVerificationCommitsOnceAsync(environment);
        await VerifyPolicyGatesEverySessionSourceAsync(environment);
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
            new NhTwoFactorReauthentication { Password = "Wrong1!Password" },
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
        environment.Policy.RequiredUserIds.Add(user.Id);

        try
        {
            await stack.AddRefreshTokenAsync(user.Id, "device-before-policy");

            // A user that the policy covers cannot get a session before enrolling.
            AssertFailure(
                await stack.Authentication.AuthenticateAsync(new AuthenticateRequest(user.UserName!, Credential)),
                NhTwoFactorFailureCodes.EnrollmentRequired);
            AssertFailure(
                await stack.Authentication.AuthenticateRefreshTokenAsync(new RefreshTokenRequest(user.UserName!, "device-before-policy")),
                NhTwoFactorFailureCodes.EnrollmentRequired);
            AssertFailure(
                await stack.Authentication.CreateSessionForVerifiedCredentialAsync(user),
                NhTwoFactorFailureCodes.EnrollmentRequired);
            AssertFailure(
                await stack.Authentication.AuthenticateExternalAsync(user.Id, NhAuthenticationFactors.MicrosoftOAuth),
                NhTwoFactorFailureCodes.EnrollmentRequired);

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
            environment.Policy.RequiredUserIds.Remove(user.Id);
        }
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
            TestTwoFactorPolicy policy)
        {
            _options = options;
            Clock = clock;
            _dataProtectionProvider = dataProtectionProvider;
            Policy = policy;
            Configuration = new NhTwoFactorConfiguration
            {
                Enabled = true,
                AuthenticatorEnabled = true,
                AuthenticatorIssuer = "NewHeap two-factor tests",
                RecoveryCodesEnabled = true,
                RecoveryCodeCount = 10,
            };
        }

        internal TestTimeProvider Clock { get; }
        internal TestTwoFactorPolicy Policy { get; }
        internal NhTwoFactorConfiguration Configuration { get; }

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
                environment.Clock);

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
                return Task.FromResult(NhTwoFactorRequirement.RequiredWith(context.EnrolledMethods));
            }

            return base.EvaluateAsync(context, cancellationToken);
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
        : NhIdentityDbContext(options);
}
