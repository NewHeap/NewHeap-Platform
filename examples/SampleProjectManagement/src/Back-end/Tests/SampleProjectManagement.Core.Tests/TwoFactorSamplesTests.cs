using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Test;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL;
using Testcontainers.PostgreSql;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Evidence for the two-factor sample: the seeded demo account signs in with a password and
/// an authenticator code, and the sample schema needs no migration for two-factor support.
/// </summary>
public sealed class TwoFactorSamplesTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
    }

    [Fact]
    public async Task SeededTwoFactorAccountCompletesTheChallengeWithAnAuthenticatorCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var application = BuildApplication(_database.GetConnectionString());
        using var scope = application.Services.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        Assert.False(context.Database.HasPendingModelChanges());
        await context.Database.MigrateAsync(cancellationToken);
        await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);

        var authenticationService = scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();
        var sampleService = Assert.IsType<SampleAuthenticationService>(authenticationService);
        Assert.True(sampleService.IsTwoFactorAvailable);

        // The password step for the enrolled demo account returns a challenge, not a session.
        var login = await sampleService.AuthenticateAsync(new AuthenticateRequest(
            SampleAuthorizationDefaults.TwoFactorEmail,
            SampleAuthorizationDefaults.Password));

        Assert.True(login.Success);
        Assert.Null(login.Data!.Session);
        var challenge = login.Data.Challenge!;
        Assert.Contains(NhTwoFactorMethods.Authenticator, challenge.Methods);

        var verified = await sampleService.VerifyTwoFactorAsync(new NhTwoFactorVerifyRequest
        {
            ChallengeToken = challenge.ChallengeToken,
            Method = NhTwoFactorMethods.Authenticator,
            Code = NhTwoFactorTestCodes.AuthenticatorCode(SampleAuthorizationDefaults.TwoFactorAuthenticatorKey),
        });

        Assert.True(verified.Success);
        Assert.NotNull(verified.Data!.Session);

        // The PIN sample follows the same two-factor policy as the password sign-in.
        var pinLogin = await sampleService.AuthenticateCustomCredentialAsync(
            SampleAuthorizationDefaults.TwoFactorEmail,
            (_, _) => Task.FromResult(true),
            cancellationToken: cancellationToken);

        Assert.True(pinLogin.Success);
        Assert.NotNull(pinLogin.Data!.Challenge);

        // Accounts without a second factor keep signing in with the password alone.
        var managerLogin = await sampleService.AuthenticateAsync(new AuthenticateRequest(
            SampleAuthorizationDefaults.ManagerEmail,
            SampleAuthorizationDefaults.Password));

        Assert.True(managerLogin.Success);
        Assert.NotNull(managerLogin.Data!.Session);
    }

    private static WebApplication BuildApplication(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://sample-project-management.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] =
                "sample-project-management-two-factor-signing-key",
        });

        builder.Services
            .AddNewHeapPlatformAspNetCommon<
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
                SampleProjectManagementDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
            {
                options.WithAuthenticationService<SampleAuthenticationService>();
                options.AddUserNamePasswordAuthentication(authentication =>
                {
                    authentication.EnableRefreshToken = true;
                    authentication.EnableDivisions = true;
                });
                options.AddTwoFactor(twoFactor => twoFactor
                    .EnableAuthenticator(authenticator => authenticator.Issuer = "Sample Project Management")
                    .EnableRecoveryCodes());
            })
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
            .WithIdentity()
            .WithDbLogService(_ => { });

        return builder.Build();
    }
}
