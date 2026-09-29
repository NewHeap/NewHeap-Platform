using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
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
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using NSubstitute;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class AuthenticationSessionProviderTests
{
    [Fact]
    public void PlatformServicesBuildWithStartupValidation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "authentication-session-tests",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] =
                "authentication-session-tests-signing-key-2026",
        });
        builder.Services.AddNewHeapPlatformAspNetCommon<
            NhUser,
            NhUserRole,
            NhDivision,
            NhDivisionUser,
            NhDivisionRole,
            NhDivisionUserRole,
            NhDivisionRoleClaim,
            NhLog,
            NhLogMessageArgument,
            NhLogFile,
            NhLogMessageTranslated,
            NhDbLogService,
            AuthenticationSessionDbContext,
            NhUserManager,
            NhDivisionService,
            NhDivisionMutateModel,
            NhDivisionUserService,
            NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build());
        builder.Services.AddSingleton(Substitute.For<IUserStore<NhUser>>());
        builder.Services.AddIdentityCore<NhUser>();

        using var application = builder.Build();
        using var scope = application.Services.CreateScope();

        Assert.IsType<NhAuthenticationSessionValidator<NhUser>>(
            scope.ServiceProvider.GetRequiredService<INhAuthenticationSessionValidator>());
    }

    [Fact]
    public async Task JwtBearerEventsComposeWithoutDroppingConsumerHandlers()
    {
        var messageOrder = new List<string>();
        var validatedOrder = new List<string>();
        Func<JwtBearerChallengeContext, Task> consumerChallenge = _ => Task.CompletedTask;
        var options = new JwtBearerOptions
        {
            Events = new JwtBearerEvents
            {
                OnMessageReceived = _ =>
                {
                    messageOrder.Add("consumer");
                    return Task.CompletedTask;
                },
                OnTokenValidated = _ =>
                {
                    validatedOrder.Add("consumer");
                    return Task.CompletedTask;
                },
                OnChallenge = consumerChallenge,
            },
        };

        NhAuthenticationEvents.Compose(
            options,
            _ =>
            {
                messageOrder.Add("newheap");
                return Task.CompletedTask;
            },
            _ =>
            {
                validatedOrder.Add("newheap");
                return Task.CompletedTask;
            });

        var httpContext = new DefaultHttpContext();
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme,
            typeof(JwtBearerHandler));

        await options.Events.OnMessageReceived(new MessageReceivedContext(httpContext, scheme, options));
        await options.Events.OnTokenValidated(new TokenValidatedContext(httpContext, scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity("Test")),
            SecurityToken = new JwtSecurityToken(),
        });

        Assert.Equal(["newheap", "consumer"], messageOrder);
        Assert.Equal(["consumer", "newheap"], validatedOrder);
        Assert.Same(consumerChallenge, options.Events.OnChallenge);
    }

    [Fact]
    public async Task SessionLifecycleWorksOnBothRelationalProviders()
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

    private static async Task VerifyProviderAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        const string initialCredential = "Old1!Password";
        const string changedCredential = "New2!Password";
        const string changedWithoutCurrentCredential = "Third3!Password";
        const string resetCredential = "Reset4!Password";
        const string customCredential = "Custom5!Password";

        var optionsBuilder = new DbContextOptionsBuilder<AuthenticationSessionDbContext>();
        configureProvider(optionsBuilder);

        await using var context = new AuthenticationSessionDbContext(optionsBuilder.Options);
        await context.Database.EnsureCreatedAsync();

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var repository = new Repository<NhUser>(context, serviceProvider);
        var identityOptions = new IdentityOptions();
        identityOptions.Tokens.PasswordResetTokenProvider = TestPasswordResetTokenProvider.ProviderName;
        var options = Options.Create(identityOptions);
        var normalizer = new UpperInvariantLookupNormalizer();
        var errors = new IdentityErrorDescriber();
        var userStore = new UserStore<NhUser, NhUserRole, AuthenticationSessionDbContext, Guid>(context);
        var roleStore = new RoleStore<NhUserRole, AuthenticationSessionDbContext, Guid>(context);

        using var roleManager = new RoleManager<NhUserRole>(
            roleStore,
            [],
            normalizer,
            errors,
            Substitute.For<ILogger<RoleManager<NhUserRole>>>());

        using var userManager = new ConsumerUserManager(
            Substitute.For<IWebHostEnvironment>(),
            userStore,
            options,
            new PasswordHasher<NhUser>(),
            [new UserValidator<NhUser>()],
            [new PasswordValidator<NhUser>()],
            normalizer,
            errors,
            serviceProvider,
            Substitute.For<ILogger<UserManager<NhUser>>>(),
            Options.Create(new MicrosoftAuthSettings()),
            repository,
            roleManager,
            new ValidationService(serviceProvider),
            Substitute.For<INhDbLogService>());

        userManager.RegisterTokenProvider(
            TestPasswordResetTokenProvider.ProviderName,
            new TestPasswordResetTokenProvider());

        var user = new NhUser
        {
            Id = Guid.NewGuid(),
            UserName = "session-user@example.test",
            Email = "session-user@example.test",
            EmailConfirmed = true,
        };

        var createResult = await userManager.CreateAsync(user, initialCredential);
        Assert.True(createResult.Succeeded);

        await AssertIssuedTokenContainsSecurityStampAsync(userManager, options, user);

        INhAuthenticationSessionValidator validator = new NhAuthenticationSessionValidator<NhUser>(userManager);
        var originalSecurityStamp = await userManager.GetSecurityStampAsync(user);
        Assert.True(await validator.ValidateAsync(CreatePrincipal(user.Id)));
        Assert.True(await validator.ValidateAsync(CreatePrincipal(user.Id, originalSecurityStamp, userManager)));

        await AddRefreshTokensAsync(context, user.Id, "existing-device");
        var authenticationService = CreateAuthenticationService(userManager, options);
        var accessFailedResult = await userManager.AccessFailedAsync(user);
        Assert.True(accessFailedResult.Succeeded);
        Assert.Equal(1, await userManager.GetAccessFailedCountAsync(user));

        var securityStampBeforeCustomAuthentication = await userManager.GetSecurityStampAsync(user);
        var customAuthentication = await authenticationService.AuthenticateVerifiedUserAsync(user);

        Assert.True(customAuthentication.Success);
        Assert.Equal(0, await userManager.GetAccessFailedCountAsync(user));
        Assert.Equal(2, await CountRefreshTokensAsync(context, user.Id));
        Assert.True(await context.Set<NhUserAuthRefreshToken>().AnyAsync(x =>
            x.UserId == user.Id && x.Token == "existing-device"));
        Assert.Equal(
            securityStampBeforeCustomAuthentication,
            await userManager.GetSecurityStampAsync(user));

        await NhRefreshTokenOperations.RevokeAllAsync(
            context.Set<NhUserAuthRefreshToken>(),
            user.Id);

        await AddRefreshTokensAsync(context, user.Id, "device-a", "device-b", "device-c");

        var caseMismatchedLogoutDeleted = await NhRefreshTokenOperations.RevokeAsync(
            context.Set<NhUserAuthRefreshToken>(),
            "DEVICE-A");
        Assert.Equal(0, caseMismatchedLogoutDeleted);
        Assert.Equal(3, await CountRefreshTokensAsync(context, user.Id));

        var logoutDeleted = await NhRefreshTokenOperations.RevokeAsync(
            context.Set<NhUserAuthRefreshToken>(),
            "device-a");
        Assert.Equal(1, logoutDeleted);
        Assert.Equal(2, await CountRefreshTokensAsync(context, user.Id));

        var caseMismatchedRefreshConsumed = await NhRefreshTokenOperations.TryConsumeAsync(
            context.Set<NhUserAuthRefreshToken>(),
            user.Id,
            "DEVICE-B",
            DateTimeOffset.UtcNow);
        var refreshConsumed = await NhRefreshTokenOperations.TryConsumeAsync(
            context.Set<NhUserAuthRefreshToken>(),
            user.Id,
            "device-b",
            DateTimeOffset.UtcNow);
        var refreshConsumedAgain = await NhRefreshTokenOperations.TryConsumeAsync(
            context.Set<NhUserAuthRefreshToken>(),
            user.Id,
            "device-b",
            DateTimeOffset.UtcNow);

        Assert.False(caseMismatchedRefreshConsumed);
        Assert.True(refreshConsumed);
        Assert.False(refreshConsumedAgain);
        Assert.Equal(1, await CountRefreshTokensAsync(context, user.Id));

        await AddRefreshTokensAsync(context, user.Id, "device-d");
        var failedPasswordChange = await userManager.ChangePasswordAsync(
            user.Id,
            new NhChangePasswordUserMutateModel
            {
                CurrentPassword = "Incorrect1!Password",
                Password = changedCredential,
                ConfirmPassword = changedCredential,
            });

        Assert.False(failedPasswordChange.Success);
        Assert.Equal(2, await CountRefreshTokensAsync(context, user.Id));

        var passwordChange = await userManager.ChangePasswordAsync(
            user.Id,
            new NhChangePasswordUserMutateModel
            {
                CurrentPassword = initialCredential,
                Password = changedCredential,
                ConfirmPassword = changedCredential,
            });

        Assert.True(passwordChange.Success);
        Assert.Equal(0, await CountRefreshTokensAsync(context, user.Id));

        var currentSecurityStamp = await userManager.GetSecurityStampAsync(user);
        var sessionMarker = await userManager.GetAuthenticationTokenAsync(
            user,
            NhAuthenticationSessionDefaults.LoginProvider,
            NhAuthenticationSessionDefaults.SecurityStampTokenName);

        Assert.Equal(currentSecurityStamp, sessionMarker);
        Assert.False(await validator.ValidateAsync(CreatePrincipal(user.Id)));
        Assert.False(await validator.ValidateAsync(CreatePrincipal(user.Id, originalSecurityStamp, userManager)));
        Assert.True(await validator.ValidateAsync(CreatePrincipal(user.Id, currentSecurityStamp, userManager)));

        await AddRefreshTokensAsync(context, user.Id, "device-e", "device-f");
        var passwordChangeWithoutCurrent = await userManager.ChangePasswordWithoutCurrentPasswordAsync(
            user.Id,
            new NhWithoutCurrentPasswordChangePasswordUserMutateModel
            {
                Password = changedWithoutCurrentCredential,
                ConfirmPassword = changedWithoutCurrentCredential,
            });

        Assert.True(passwordChangeWithoutCurrent.Success);
        Assert.Equal(0, await CountRefreshTokensAsync(context, user.Id));

        var securityStampAfterPasswordChangeWithoutCurrent = await userManager.GetSecurityStampAsync(user);
        Assert.False(await validator.ValidateAsync(CreatePrincipal(user.Id, currentSecurityStamp, userManager)));
        Assert.True(await validator.ValidateAsync(CreatePrincipal(
            user.Id,
            securityStampAfterPasswordChangeWithoutCurrent,
            userManager)));

        await AddRefreshTokensAsync(context, user.Id, "device-g", "device-h");
        var passwordResetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var passwordReset = await userManager.ResetPasswordAsync(
            user.Id,
            new NhResetPasswordUserMutateModel
            {
                UserId = user.Id,
                Token = passwordResetToken,
                Password = resetCredential,
                ConfirmPassword = resetCredential,
            });

        Assert.True(passwordReset.Success);
        Assert.Equal(0, await CountRefreshTokensAsync(context, user.Id));

        var securityStampAfterReset = await userManager.GetSecurityStampAsync(user);
        Assert.False(await validator.ValidateAsync(CreatePrincipal(
            user.Id,
            securityStampAfterPasswordChangeWithoutCurrent,
            userManager)));
        Assert.True(await validator.ValidateAsync(CreatePrincipal(user.Id, securityStampAfterReset, userManager)));
        Assert.Equal(
            securityStampAfterReset,
            await userManager.GetAuthenticationTokenAsync(
                user,
                NhAuthenticationSessionDefaults.LoginProvider,
                NhAuthenticationSessionDefaults.SecurityStampTokenName));
        Assert.True(await userManager.CheckPasswordAsync(user, resetCredential));

        await AddRefreshTokensAsync(context, user.Id, "consumer-device-a", "consumer-device-b");
        var consumerPasswordChange = await userManager.ChangeConsumerPasswordAsync(
            user,
            resetCredential,
            customCredential);

        Assert.True(consumerPasswordChange.Success);
        Assert.Equal(0, await CountRefreshTokensAsync(context, user.Id));
        Assert.True(await userManager.CheckPasswordAsync(user, customCredential));

        await VerifyImpersonationSessionHasNoRefreshTokenAsync(context, userManager, options, user);
    }

    /// <summary>
    /// An impersonation session must not receive a refresh token: rotating it would rebuild
    /// the token from the target user's claims, drop the impersonation origin and leave the
    /// administrator with an ordinary long-lived login as the target user.
    /// </summary>
    private static async Task VerifyImpersonationSessionHasNoRefreshTokenAsync(
        AuthenticationSessionDbContext context,
        NhUserManager userManager,
        IOptions<IdentityOptions> identityOptions,
        NhUser administrator)
    {
        var target = new NhUser
        {
            Id = Guid.NewGuid(),
            UserName = "impersonated-user@example.test",
            Email = "impersonated-user@example.test",
            EmailConfirmed = true,
        };
        var createTargetResult = await userManager.CreateAsync(target, "Target6!Password");
        Assert.True(createTargetResult.Succeeded);

        await AddRefreshTokensAsync(context, target.Id, "target-own-device");
        var administratorRefreshTokens = await CountRefreshTokensAsync(context, administrator.Id);
        var authenticationService = CreateAuthenticationService(userManager, identityOptions);

        var impersonation = await authenticationService.Impersonate(
            administrator.Id,
            new ImpersonateRequest(target.Id));

        Assert.True(impersonation.Success);
        Assert.Null(impersonation.Data!.RefreshToken);
        Assert.Null(impersonation.Data.RefreshValidTo);
        Assert.Equal(1, await CountRefreshTokensAsync(context, target.Id));
        Assert.Equal(administratorRefreshTokens, await CountRefreshTokensAsync(context, administrator.Id));

        var impersonationToken = new JwtSecurityTokenHandler().ReadJwtToken(impersonation.Data.Token);
        Assert.Equal(
            administrator.Id.ToString(),
            impersonationToken.Claims.Single(x => x.Type == NhPlatformClaimTypes.ImpersonateOriginUserId).Value);

        // Reverting restores the administrator with a normal session of its own.
        var revert = await authenticationService.ImpersonateRevert(target.Id, administrator.Id);

        Assert.True(revert.Success);
        Assert.NotNull(revert.Data!.RefreshToken);
        Assert.Equal(administratorRefreshTokens + 1, await CountRefreshTokensAsync(context, administrator.Id));
        Assert.Equal(1, await CountRefreshTokensAsync(context, target.Id));
        Assert.DoesNotContain(
            new JwtSecurityTokenHandler().ReadJwtToken(revert.Data.Token).Claims,
            x => x.Type == NhPlatformClaimTypes.ImpersonateOriginUserId);
    }

    private static async Task AssertIssuedTokenContainsSecurityStampAsync(
        NhUserManager userManager,
        IOptions<IdentityOptions> identityOptions,
        NhUser user)
    {
        var authenticationService = CreateAuthenticationService(userManager, identityOptions);

        var token = await authenticationService.CreateToken(
            user.Id,
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
            TimeSpan.FromMinutes(5));
        var securityStampClaimType = userManager.Options.ClaimsIdentity.SecurityStampClaimType;

        Assert.Equal(
            await userManager.GetSecurityStampAsync(user),
            token.Claims.Single(x => x.Type == securityStampClaimType).Value);
    }

    private static TestAuthenticationService CreateAuthenticationService(
        NhUserManager userManager,
        IOptions<IdentityOptions> identityOptions)
    {
        var signInManager = new SignInManager<NhUser>(
            userManager,
            Substitute.For<IHttpContextAccessor>(),
            new UserClaimsPrincipalFactory<NhUser>(userManager, identityOptions),
            identityOptions,
            Substitute.For<ILogger<SignInManager<NhUser>>>(),
            Substitute.For<IAuthenticationSchemeProvider>(),
            Substitute.For<IUserConfirmation<NhUser>>());

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://auth.example.test",
                ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "test-signing-key-with-at-least-thirty-two-bytes",
            })
            .Build();

        return new TestAuthenticationService(
            signInManager,
            userManager,
            Substitute.For<ILogger<AuthenticationService>>(),
            configuration,
            new TokenValidationParameters(),
            new AuthenticationConfiguration());
    }

    private static ClaimsPrincipal CreatePrincipal(
        Guid userId,
        string? securityStamp = null,
        UserManager<NhUser>? userManager = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
        };

        if (securityStamp != null)
        {
            claims.Add(new Claim(
                userManager!.Options.ClaimsIdentity.SecurityStampClaimType,
                securityStamp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static async Task AddRefreshTokensAsync(
        AuthenticationSessionDbContext context,
        Guid userId,
        params string[] tokens)
    {
        foreach (var token in tokens)
        {
            context.Set<NhUserAuthRefreshToken>().Add(new NhUserAuthRefreshToken
            {
                UserId = userId,
                Token = token,
                ExpiryDateTime = DateTimeOffset.UtcNow.AddDays(1),
            });
        }

        await context.SaveChangesAsync();
    }

    private static Task<int> CountRefreshTokensAsync(
        AuthenticationSessionDbContext context,
        Guid userId)
    {
        return context.Set<NhUserAuthRefreshToken>().CountAsync(x => x.UserId == userId);
    }

    private sealed class ConsumerUserManager : NhUserManager
    {
        internal ConsumerUserManager(
            IWebHostEnvironment environment,
            IUserStore<NhUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<NhUser> passwordHasher,
            IEnumerable<IUserValidator<NhUser>> userValidators,
            IEnumerable<IPasswordValidator<NhUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<NhUser>> logger,
            IOptions<MicrosoftAuthSettings> microsoftAuthSettings,
            IRepository<NhUser> userRepository,
            RoleManager<NhUserRole> roleManager,
            ValidationService validationService,
            INhDbLogService dbLogService)
            : base(
                environment,
                store,
                optionsAccessor,
                passwordHasher,
                userValidators,
                passwordValidators,
                keyNormalizer,
                errors,
                services,
                logger,
                microsoftAuthSettings,
                userRepository,
                roleManager,
                validationService,
                dbLogService)
        {
        }

        internal Task<TaskResult> ChangeConsumerPasswordAsync(
            NhUser user,
            string currentPassword,
            string newPassword)
        {
            return ExecutePasswordMutationWithSessionInvalidationAsync(
                user,
                () => ChangePasswordAsync(user, currentPassword, newPassword),
                "Consumer password change successful.",
                user.Id,
                CancellationToken.None);
        }
    }

    private sealed class TestAuthenticationService : NhAuthenticationService<
        NhUser,
        NhDivision,
        NhDivisionUser,
        NhDivisionRole,
        NhDivisionUserRole,
        NhDivisionRoleClaim>
    {
        internal TestAuthenticationService(
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

        internal Task<TaskResult<UserToken>> AuthenticateVerifiedUserAsync(NhUser user)
        {
            return CreateAuthenticationSessionAsync(user);
        }
    }

    private sealed class TestPasswordResetTokenProvider : IUserTwoFactorTokenProvider<NhUser>
    {
        internal const string ProviderName = "TestPasswordReset";
        private const string Token = "valid-reset-token";

        public Task<string> GenerateAsync(string purpose, UserManager<NhUser> manager, NhUser user)
        {
            return Task.FromResult(Token);
        }

        public Task<bool> ValidateAsync(
            string purpose,
            string token,
            UserManager<NhUser> manager,
            NhUser user)
        {
            return Task.FromResult(token == Token);
        }

        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<NhUser> manager, NhUser user)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class AuthenticationSessionDbContext(
        DbContextOptions<AuthenticationSessionDbContext> options)
        : NhIdentityDbContext(options);
}
