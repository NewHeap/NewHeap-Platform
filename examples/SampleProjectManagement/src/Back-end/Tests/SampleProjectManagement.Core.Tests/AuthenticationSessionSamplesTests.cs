using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Evidence for the session cases with the API's own authentication composition: the refresh-token
/// table holds only digests of the tokens, a blocked account receives no session and cannot refresh,
/// and an impersonation ends with the session of the user who started it.
/// </summary>
public sealed class AuthenticationSessionSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private const string ChangedPassword = "Changed2!Sample";

    [Fact]
    public async Task RefreshTokensAreStoredAsDigestsAndABlockedAccountGetsNoSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var application = BuildApplication(await postgreSql.CreateMigratedDatabaseAsync("authentication_refresh"));
        using var scope = application.Services.CreateScope();
        await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);

        var authentication = scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<INhUserManager<NhUser>>();
        var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
        var manager = (await userManager.FindByNameAsync(SampleAuthorizationDefaults.ManagerEmail))!;
        var credentials = new AuthenticateRequest(SampleAuthorizationDefaults.ManagerEmail, SampleAuthorizationDefaults.Password);

        var login = await authentication.Authenticate(credentials);
        Assert.True(login.Success);
        var refreshToken = login.Data!.RefreshToken!;

        // The table holds a keyed digest of the token, so a copy of the row refreshes nothing.
        var storedToken = await context.Set<NhUserAuthRefreshToken>()
            .Where(x => x.UserId == manager.Id)
            .Select(x => x.Token)
            .SingleAsync(cancellationToken);
        Assert.StartsWith("nhr1:", storedToken, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, storedToken, StringComparison.Ordinal);
        Assert.False((await authentication.AuthenticateRefreshTokenAsync(
            new RefreshTokenRequest(SampleAuthorizationDefaults.ManagerEmail, storedToken))).Success);

        // An administrator blocks the account without an end date. Sign-in and refresh are refused,
        // the refresh like an unknown token, and the token is not consumed.
        await userManager.UpdateUserLockout(manager, DateTimeOffset.UtcNow.AddMinutes(-1), end: null, cancellationToken);

        var blockedLogin = await authentication.Authenticate(credentials);
        Assert.False(blockedLogin.Success);
        Assert.Contains("User locked out", blockedLogin.AllErrorMessages.Select(message => message.ToString()));

        var blockedRefresh = await authentication.AuthenticateRefreshTokenAsync(
            new RefreshTokenRequest(SampleAuthorizationDefaults.ManagerEmail, refreshToken));
        Assert.False(blockedRefresh.Success);
        Assert.Equal(["Invalid refresh token"], blockedRefresh.AllErrorMessages.Select(message => message.ToString()));
        Assert.Equal(
            [storedToken],
            await context.Set<NhUserAuthRefreshToken>()
                .Where(x => x.UserId == manager.Id)
                .Select(x => x.Token)
                .ToListAsync(cancellationToken));

        // Once the block is lifted the same token refreshes.
        await userManager.UpdateUserLockout(manager, start: null, end: null, cancellationToken);
        var refreshed = await authentication.AuthenticateRefreshTokenAsync(
            new RefreshTokenRequest(SampleAuthorizationDefaults.ManagerEmail, refreshToken));
        Assert.True(refreshed.Success);
        Assert.NotEqual(refreshToken, refreshed.Data!.RefreshToken);
    }

    [Fact]
    public async Task AnImpersonationEndsWithTheSessionOfTheUserWhoStartedIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var application = BuildApplication(await postgreSql.CreateMigratedDatabaseAsync("authentication_impersonation"));
        using var scope = application.Services.CreateScope();
        await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);

        var authentication = scope.ServiceProvider.GetRequiredService<INhAuthenticationService>();
        var userManager = scope.ServiceProvider.GetRequiredService<INhUserManager<NhUser>>();
        var sessionValidator = scope.ServiceProvider.GetRequiredService<INhAuthenticationSessionValidator>();
        var manager = (await userManager.FindByNameAsync(SampleAuthorizationDefaults.ManagerEmail))!;
        var viewer = (await userManager.FindByNameAsync(SampleAuthorizationDefaults.ViewerEmail))!;

        var impersonation = await authentication.Impersonate(manager.Id, new ImpersonateRequest(viewer.Id));
        Assert.True(impersonation.Success);
        Assert.Null(impersonation.Data!.RefreshToken);
        var principal = PrincipalOf(impersonation.Data.Token);
        Assert.True(await sessionValidator.ValidateAsync(principal, cancellationToken));

        // Blocking the manager ends the impersonation, and revert restores no session of the manager.
        await userManager.UpdateUserLockout(manager, DateTimeOffset.UtcNow.AddMinutes(-1), end: null, cancellationToken);
        Assert.False(await sessionValidator.ValidateAsync(principal, cancellationToken));
        Assert.False((await authentication.ImpersonateRevert(viewer.Id, manager.Id)).Success);

        await userManager.UpdateUserLockout(manager, start: null, end: null, cancellationToken);
        Assert.True(await sessionValidator.ValidateAsync(principal, cancellationToken));

        // A password change of the manager ends every session of the manager, the impersonation included.
        var passwordChange = await userManager.ChangePasswordWithoutCurrentPasswordAsync(
            manager.Id,
            new NhWithoutCurrentPasswordChangePasswordUserMutateModel
            {
                Password = ChangedPassword,
                ConfirmPassword = ChangedPassword,
            },
            cancellationToken: cancellationToken);
        Assert.True(passwordChange.Success);
        Assert.False(await sessionValidator.ValidateAsync(principal, cancellationToken));
    }

    /// <summary>
    /// The principal that bearer validation makes of <paramref name="accessToken"/>; NewHeap clears
    /// the inbound claim map, so the token's claim types are kept.
    /// </summary>
    private static ClaimsPrincipal PrincipalOf(string accessToken)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims,
            "Bearer"));
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
                "sample-project-management-session-signing-key",
            ["TwoFactor:PasskeyServerDomain"] = "localhost",
            ["TwoFactor:PasskeyAllowedOrigins:0"] = "http://localhost:4210",
        });

        // The authentication part of the API's composition in Program.cs.
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
                    authentication.EnableImpersonate = true;
                });
                options.AddTwoFactor(twoFactor => twoFactor.ConfigureSampleTwoFactor(builder.Configuration));
            })
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
            .WithIdentity()
            .WithDbLogService(_ => { });

        return builder.Build();
    }
}
