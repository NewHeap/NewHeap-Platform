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
/// and cannot reach Sample South, where it is a member without the manage permission.
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

    private async Task<WebApplication> BuildSeededApplicationAsync(string databaseName)
    {
        var application = BuildApplication(await postgreSql.CreateMigratedDatabaseAsync(databaseName));
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

    private static WebApplication BuildApplication(string connectionString)
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

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthorization(options => options.AddSampleDivisionPolicies());

        return builder.Build();
    }
}
