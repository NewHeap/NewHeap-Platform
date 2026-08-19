using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using NSubstitute;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;
using System.Security.Claims;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class ProjectAuthorizationSamplesTests
{
    [Fact]
    public async Task SystemPermissionGrantsProjectAccessWithoutAResourceClaim()
    {
        var requirement = new ProjectAccessRequirement("confidential.view");
        var principal = CreatePrincipal(new Claim(
            NhPlatformClaimTypes.Permission,
            requirement.SystemPermission));
        var context = new AuthorizationHandlerContext([requirement], principal, null);
        var handler = new ProjectAccessHandler(new HttpContextAccessor());

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal("app.project.confidential.view", requirement.SystemPermission);
        Assert.Equal("project.confidential.view", requirement.DivisionPermission);
    }

    [Fact]
    public async Task ProjectPermissionGrantsOnlyTheMatchingProjectInTheActiveDivision()
    {
        await using var dbContext = CreateDbContext();
        var outsideActiveDivisionProjectId = Guid.NewGuid();
        dbContext.Projects.AddRange(
            CreateProject(
                SampleAuthorizationDefaults.AlphaProjectId,
                SampleAuthorizationDefaults.NorthDivisionId,
                "AUTH-ALPHA"),
            CreateProject(
                SampleAuthorizationDefaults.BetaProjectId,
                SampleAuthorizationDefaults.NorthDivisionId,
                "AUTH-BETA"),
            CreateProject(
                outsideActiveDivisionProjectId,
                SampleAuthorizationDefaults.SouthDivisionId,
                "AUTH-SOUTH"));
        await dbContext.SaveChangesAsync();

        var requirement = new ProjectAccessRequirement("confidential.view");
        var projectClaim = new Claim(
            SampleProjectClaimTypes.ProjectPermission,
            SampleProjectClaimTypes.PermissionValue(
                SampleAuthorizationDefaults.AlphaProjectId,
                requirement.Permission));

        var allowed = await EvaluateAsync(
            dbContext,
            requirement,
            CreatePrincipal(projectClaim),
            SampleAuthorizationDefaults.NorthDivisionId,
            SampleAuthorizationDefaults.AlphaProjectId);
        var deniedForDifferentProject = await EvaluateAsync(
            dbContext,
            requirement,
            CreatePrincipal(projectClaim),
            SampleAuthorizationDefaults.NorthDivisionId,
            SampleAuthorizationDefaults.BetaProjectId);
        var deniedOutsideActiveDivision = await EvaluateAsync(
            dbContext,
            requirement,
            CreatePrincipal(new Claim(
                SampleProjectClaimTypes.ProjectPermission,
                SampleProjectClaimTypes.PermissionValue(
                    outsideActiveDivisionProjectId,
                    requirement.Permission))),
            SampleAuthorizationDefaults.NorthDivisionId,
            outsideActiveDivisionProjectId);

        Assert.True(allowed);
        Assert.False(deniedForDifferentProject);
        Assert.False(deniedOutsideActiveDivision);
    }

    [Fact]
    public async Task DivisionPermissionCannotGrantAProjectOutsideTheActiveDivision()
    {
        await using var dbContext = CreateDbContext();
        var outsideActiveDivisionProjectId = Guid.NewGuid();
        dbContext.Projects.AddRange(
            CreateProject(
                SampleAuthorizationDefaults.AlphaProjectId,
                SampleAuthorizationDefaults.NorthDivisionId,
                "AUTH-ALPHA"),
            CreateProject(
                outsideActiveDivisionProjectId,
                SampleAuthorizationDefaults.SouthDivisionId,
                "AUTH-SOUTH"));
        await dbContext.SaveChangesAsync();

        var requirement = new ProjectAccessRequirement("confidential.view");
        var principal = CreatePrincipal();
        var allowed = await EvaluateAsync(
            dbContext,
            requirement,
            principal,
            SampleAuthorizationDefaults.NorthDivisionId,
            SampleAuthorizationDefaults.AlphaProjectId,
            divisionAccessGranted: true);
        var denied = await EvaluateAsync(
            dbContext,
            requirement,
            principal,
            SampleAuthorizationDefaults.NorthDivisionId,
            outsideActiveDivisionProjectId,
            divisionAccessGranted: true);

        Assert.True(allowed);
        Assert.False(denied);
    }

    private static async Task<bool> EvaluateAsync(
        SampleProjectManagementDbContext dbContext,
        ProjectAccessRequirement requirement,
        ClaimsPrincipal principal,
        Guid activeDivisionId,
        Guid projectId,
        bool divisionAccessGranted = false)
    {
        var userManager = Substitute.For<INhUserManager>();
        userManager
            .GetValidClaimsByUserIdAsync(
                Arg.Any<Guid>(),
                true,
                Arg.Any<CancellationToken>())
            .Returns([]);
        userManager
            .DivisionAccessAsync(
                Arg.Any<Guid?>(),
                Arg.Any<IEnumerable<Claim>>(),
                Arg.Any<IEnumerable<Claim>?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(divisionAccessGranted);

        var requestServices = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(userManager)
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = requestServices,
            User = principal
        };
        httpContext.Request.Headers["X-NH-ActiveDivisionId"] = activeDivisionId.ToString();
        httpContext.Request.RouteValues[requirement.ProjectRouteValueName] = projectId.ToString();

        var authorizationContext = new AuthorizationHandlerContext(
            [requirement],
            principal,
            null);
        var handler = new ProjectAccessHandler(new HttpContextAccessor
        {
            HttpContext = httpContext
        });

        await handler.HandleAsync(authorizationContext);
        return authorizationContext.HasSucceeded;
    }

    private static SampleProjectManagementDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SampleProjectManagementDbContext(options);
    }

    private static Project CreateProject(Guid id, Guid divisionId, string key) =>
        new()
        {
            Id = id,
            DivisionId = divisionId,
            Key = key,
            Name = key,
            Status = ProjectStatus.Active
        };

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                .. claims
            ],
            "sample-test"));
}
