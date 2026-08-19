using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.DAL;
using System.Security.Claims;

namespace SampleProjectManagement.Api.Authorization;

public sealed class ProjectAccessHandler : AuthorizationHandler<ProjectAccessRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ProjectAccessHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ProjectAccessRequirement requirement)
    {
        if (context.User.HasClaim(
                NhPlatformClaimTypes.Permission,
                requirement.SystemPermission) ||
            context.User.HasClaim(
                NhPlatformClaimTypes.Permission,
                NewHeap.Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            context.Succeed(requirement);
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out _))
        {
            return;
        }

        var activeDivisionId = httpContext.GetActiveDivisionId();
        if (!activeDivisionId.HasValue ||
            !TryGetProjectId(httpContext, requirement.ProjectRouteValueName, out var projectId))
        {
            return;
        }

        var dbContext = httpContext.RequestServices
            .GetRequiredService<SampleProjectManagementDbContext>();
        var belongsToActiveDivision = await dbContext.Projects
            .AsNoTracking()
            .AnyAsync(
                project =>
                    project.Id == projectId &&
                    project.DivisionId == activeDivisionId.Value,
                httpContext.RequestAborted);
        if (!belongsToActiveDivision)
        {
            return;
        }

        if (context.User.HasClaim(
                SampleProjectClaimTypes.ProjectPermission,
                SampleProjectClaimTypes.PermissionValue(projectId, requirement.Permission)))
        {
            context.Succeed(requirement);
            return;
        }

        if (await httpContext.HasDivisionAccessAsync(
                activeDivisionId,
                [new Claim(
                    NhPlatformClaimTypes.DivisionPermission,
                    requirement.DivisionPermission)],
                cancellationToken: httpContext.RequestAborted))
        {
            context.Succeed(requirement);
        }
    }

    private static bool TryGetProjectId(
        HttpContext httpContext,
        string routeValueName,
        out Guid projectId)
    {
        return Guid.TryParse(
            httpContext.Request.RouteValues[routeValueName]?.ToString(),
            out projectId);
    }
}
