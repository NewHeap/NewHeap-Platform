using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL;
using System.Security.Claims;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-211 evidence with the API's own division policies, controller and seeded identities: the
/// manager manages the memberships of Sample North, the division in the active-division header,
/// and cannot reach Sample South, where it is a member without the manage permission. A lockout
/// the manager sets counts in whole UTC days of the application's clock, and an account without
/// an active division or memberships keeps its application claims.
/// </summary>
public sealed class DivisionMembershipSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private static readonly Guid North = SampleAuthorizationDefaults.NorthDivisionId;
    private static readonly Guid South = SampleAuthorizationDefaults.SouthDivisionId;

    [Fact]
    public async Task ADivisionManagerManagesTheMembershipsOfTheActiveDivisionOnly()
    {
        await using var application = await BuildSeededApplicationAsync("division_membership_writes");
        var manager = await FindUserIdAsync(application, SampleAuthorizationDefaults.ManagerEmail);
        var securityOfficer = await FindUserIdAsync(application, SampleAuthorizationDefaults.SecurityOfficerEmail);
        var managerInSouth = await FindMembershipIdAsync(application, manager, South);
        var memberRole = await FindDivisionRoleIdAsync(application, SampleAuthorizationDefaults.ProjectMemberRole);
        var managerRole = await FindDivisionRoleIdAsync(application, SampleAuthorizationDefaults.DivisionManagerRole);

        // The manage policy follows the division in the header.
        Assert.True(await AuthorizeAsync(application, manager, North, SampleDivisionPolicies.Manage));
        Assert.False(await AuthorizeAsync(application, manager, South, SampleDivisionPolicies.Manage));

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var controller = CreateController(scope, manager, North);

            // With Sample North active, Sample South stays out of reach.
            Assert.IsType<ForbidResult>(await controller.Create(Membership(South, securityOfficer, managerRole)));
            Assert.IsType<ForbidResult>(await controller.Update(managerInSouth, Membership(South, manager, managerRole)));
            Assert.IsType<NotFoundResult>(await controller.Update(managerInSouth, Membership(North, manager, managerRole)));
            Assert.IsType<NotFoundResult>(await controller.GetById(managerInSouth));
            Assert.IsType<NotFoundResult>(await controller.Delete(managerInSouth, TestContext.Current.CancellationToken));

            // Sample North itself is managed as usual.
            Assert.IsType<OkObjectResult>(await controller.Create(Membership(North, securityOfficer, memberRole)));
        }

        await using (var scope = application.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
            var southRoles = await context.DivisionUserRoles
                .Where(role => role.DivisionUserId == managerInSouth)
                .Select(role => role.DivisionRoleId)
                .ToListAsync(TestContext.Current.CancellationToken);

            Assert.Equal([memberRole], southRoles);
            Assert.False(await context.DivisionUsers.AnyAsync(
                membership => membership.UserId == securityOfficer && membership.DivisionId == South,
                TestContext.Current.CancellationToken));
            Assert.True(await context.DivisionUsers.AnyAsync(
                membership => membership.UserId == securityOfficer && membership.DivisionId == North,
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task MembershipAloneDecidesADivisionCheckWithoutRequirements()
    {
        await using var application = await BuildSeededApplicationAsync("division_membership_access");
        var manager = await FindUserIdAsync(application, SampleAuthorizationDefaults.ManagerEmail);
        var divisionEditor = await FindUserIdAsync(application, SampleAuthorizationDefaults.DivisionEditorEmail);
        var securityOfficer = await FindUserIdAsync(application, SampleAuthorizationDefaults.SecurityOfficerEmail);

        // The gate of the division-exclusive operations: the division editor works in Sample North
        // only, and a division id that does not exist is no division to work in.
        Assert.True(await HasDivisionAccessAsync(application, divisionEditor, North));
        Assert.False(await HasDivisionAccessAsync(application, divisionEditor, South));
        Assert.False(await HasDivisionAccessAsync(application, divisionEditor, Guid.NewGuid()));
        Assert.False(await MayWorkInAsync(application, divisionEditor, South));
        Assert.False(await HasDivisionAccessAsync(application, securityOfficer, North));

        // A member without division roles is a member all the same, without any division permission.
        await using (var scope = application.Services.CreateAsyncScope())
        {
            var membership = new NhDivisionUserMutateModel { DivisionId = North, UserId = securityOfficer };
            Assert.IsType<OkObjectResult>(await CreateController(scope, manager, North).Create(membership));
        }

        Assert.True(await HasDivisionAccessAsync(application, securityOfficer, North));
        Assert.True(await MayWorkInAsync(application, securityOfficer, North));
        Assert.False(await HasProjectViewAsync(application, securityOfficer, North));
    }

    /// <summary>The division check without requirements, as the sample's operations gate it.</summary>
    private static async Task<bool> HasDivisionAccessAsync(WebApplication application, Guid userId, Guid divisionId)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var request = CreateRequest(scope, userId, divisionId);

        return await request.HasDivisionAccessAsync(divisionId, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>The platform's question for background work and agents, outside a request.</summary>
    private static async Task<bool> MayWorkInAsync(WebApplication application, Guid userId, Guid divisionId)
    {
        await using var scope = application.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<INhDivisionAccess>()
            .HasAccessAsync(NhActorRef.ForUser(userId), divisionId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ALockedOutMemberLosesTheDivisionUntilTheLockoutEnds()
    {
        await using var application = await BuildSeededApplicationAsync("division_membership_lockout");
        var manager = await FindUserIdAsync(application, SampleAuthorizationDefaults.ManagerEmail);
        var divisionEditor = await FindUserIdAsync(application, SampleAuthorizationDefaults.DivisionEditorEmail);
        var editorInNorth = await FindMembershipIdAsync(application, divisionEditor, North);
        var editorRole = await FindDivisionRoleIdAsync(application, SampleAuthorizationDefaults.DivisionEditorRole);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await HasProjectViewAsync(application, divisionEditor, North));

        // The manager suspends the division editor's membership of Sample North for a week.
        var lockout = Membership(North, divisionEditor, editorRole);
        lockout.LockOutStartDateTime = now.AddDays(-1);
        lockout.LockOutEndDateTime = now.AddDays(6);
        await using (var scope = application.Services.CreateAsyncScope())
        {
            Assert.IsType<OkResult>(await CreateController(scope, manager, North).Update(editorInNorth, lockout));
        }

        // Every request reloads the claims, so the lockout applies at once, while other members of
        // Sample North keep their access.
        Assert.Empty(await DivisionClaimsAsync(application, divisionEditor, North));
        Assert.False(await HasProjectViewAsync(application, divisionEditor, North));
        Assert.True(await HasProjectViewAsync(application, manager, North));

        // Once the lockout has ended, the membership counts again.
        lockout.LockOutEndDateTime = now.AddDays(-1);
        await using (var scope = application.Services.CreateAsyncScope())
        {
            Assert.IsType<OkResult>(await CreateController(scope, manager, North).Update(editorInNorth, lockout));
        }

        Assert.NotEmpty(await DivisionClaimsAsync(application, divisionEditor, North));
        Assert.True(await HasProjectViewAsync(application, divisionEditor, North));
    }

    [Fact]
    public async Task ALockoutCountsInWholeUtcDaysOfTheApplicationClock()
    {
        // The application's clock, moved to noon UTC so the test never runs across midnight.
        var clock = new SampleTestClock();
        var startedAt = clock.GetUtcNow();
        var noon = new DateTimeOffset(startedAt.UtcDateTime.Date.AddHours(12), TimeSpan.Zero);
        clock.Advance(noon > startedAt ? noon - startedAt : noon.AddDays(1) - startedAt);
        var todayStart = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);

        await using var application = await BuildSeededApplicationAsync("division_membership_lockout_days", clock);
        var manager = await FindUserIdAsync(application, SampleAuthorizationDefaults.ManagerEmail);
        var divisionEditor = await FindUserIdAsync(application, SampleAuthorizationDefaults.DivisionEditorEmail);
        var securityOfficer = await FindUserIdAsync(application, SampleAuthorizationDefaults.SecurityOfficerEmail);
        var editorInNorth = await FindMembershipIdAsync(application, divisionEditor, North);
        var editorRole = await FindDivisionRoleIdAsync(application, SampleAuthorizationDefaults.DivisionEditorRole);

        // The manager suspends the division editor's membership of Sample North through today: the
        // lockout ends at midnight UTC this morning, and the end day counts whole.
        var lockout = Membership(North, divisionEditor, editorRole);
        lockout.LockOutStartDateTime = todayStart.AddDays(-3);
        lockout.LockOutEndDateTime = todayStart;
        await using (var scope = application.Services.CreateAsyncScope())
        {
            Assert.IsType<OkResult>(await CreateController(scope, manager, North).Update(editorInNorth, lockout));
        }

        Assert.Empty(await DivisionClaimsAsync(application, divisionEditor, North));
        Assert.False(await HasProjectViewAsync(application, divisionEditor, North));

        // On the next UTC day of the application's clock the membership counts again, without a
        // change to the row.
        clock.Advance(TimeSpan.FromDays(1));
        Assert.NotEmpty(await DivisionClaimsAsync(application, divisionEditor, North));
        Assert.True(await HasProjectViewAsync(application, divisionEditor, North));

        // The security officer has no active division and no membership: the claims that sign-in
        // and the division policies load hold the application permissions and no division.
        await using (var scope = application.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();
            Assert.Null(await context.Users
                .Where(user => user.Id == securityOfficer)
                .Select(user => user.ActiveDivisionId)
                .SingleAsync(TestContext.Current.CancellationToken));
            Assert.False(await context.DivisionUsers.AnyAsync(
                membership => membership.UserId == securityOfficer,
                TestContext.Current.CancellationToken));

            var claims = await scope.ServiceProvider
                .GetRequiredService<INhUserManager>()
                .GetValidClaimsByUserIdAsync(securityOfficer, withDivision: true, TestContext.Current.CancellationToken);
            Assert.Contains(claims, claim => claim.Type == NhPlatformClaimTypes.Permission && claim.Value == "app.project.view");
            Assert.DoesNotContain(claims, claim => claim.Type == NhPlatformClaimTypes.DivisionMember);
        }

        Assert.False(await HasDivisionAccessAsync(application, securityOfficer, North));
    }

    private static async Task<List<string>> DivisionClaimsAsync(WebApplication application, Guid userId, Guid divisionId)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var claims = await scope.ServiceProvider
            .GetRequiredService<INhUserManager>()
            .GetValidClaimsByUserIdAsync(userId, withDivision: true, TestContext.Current.CancellationToken);

        return claims
            .Where(claim => claim.Value.StartsWith($"{divisionId}_", StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .ToList();
    }

    /// <summary>The division check of the sample's project policies, from the current claims.</summary>
    private static async Task<bool> HasProjectViewAsync(WebApplication application, Guid userId, Guid divisionId)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var request = CreateRequest(scope, userId, divisionId);

        return await request.HasDivisionAccessAsync(
            divisionId,
            [new Claim(NhPlatformClaimTypes.DivisionPermission, "project.view")],
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<WebApplication> BuildSeededApplicationAsync(string databaseName, TimeProvider? clock = null)
    {
        var application = BuildApplication(await postgreSql.CreateMigratedDatabaseAsync(databaseName), clock);
        await using var scope = application.Services.CreateAsyncScope();
        await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);

        return application;
    }

    private static async Task<Guid> FindUserIdAsync(WebApplication application, string email)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();

        return await context.Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> FindMembershipIdAsync(WebApplication application, Guid userId, Guid divisionId)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();

        return await context.DivisionUsers
            .Where(membership => membership.UserId == userId && membership.DivisionId == divisionId)
            .Select(membership => membership.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> FindDivisionRoleIdAsync(WebApplication application, string name)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SampleProjectManagementDbContext>();

        return await context.DivisionRoles
            .Where(role => role.Name == name)
            .Select(role => role.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Evaluates a policy as a request of the user with the active-division header.</summary>
    private static async Task<bool> AuthorizeAsync(WebApplication application, Guid userId, Guid activeDivisionId, string policy)
    {
        await using var scope = application.Services.CreateAsyncScope();
        var httpContext = CreateRequest(scope, userId, activeDivisionId);
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();

        return (await authorization.AuthorizeAsync(httpContext.User, null, policy)).Succeeded;
    }

    private static DivisionMembershipController CreateController(AsyncServiceScope scope, Guid userId, Guid activeDivisionId)
    {
        var controller = ActivatorUtilities.CreateInstance<DivisionMembershipController>(scope.ServiceProvider);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = CreateRequest(scope, userId, activeDivisionId)
        };

        return controller;
    }

    private static DefaultHttpContext CreateRequest(AsyncServiceScope scope, Guid userId, Guid activeDivisionId)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim(NhPlatformClaimTypes.Permission, "app.project.view")
                ],
                "sample-test"))
        };
        httpContext.Request.Headers[NewHeap.Platform.AspNet.Common.Constants.HttpHeaderKeys.ActiveDivisionId] = activeDivisionId.ToString();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        return httpContext;
    }

    private static NhDivisionUserMutateModel Membership(Guid divisionId, Guid userId, Guid roleId)
    {
        return new NhDivisionUserMutateModel
        {
            DivisionId = divisionId,
            UserId = userId,
            RoleIds = [roleId]
        };
    }

    private static WebApplication BuildApplication(string connectionString, TimeProvider? clock)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://sample-project-management.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] =
                "sample-project-management-division-signing-key",
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
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
            .WithIdentity()
            .WithDbLogService(_ => { });

        // The application's clock decides the UTC day of division lockouts; without one, the
        // system clock does.
        if (clock is not null)
        {
            builder.Services.AddSingleton(clock);
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthorization(options => options.AddSampleDivisionPolicies());

        return builder.Build();
    }
}
