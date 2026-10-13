using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy.Requirements;
using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;

/// <summary>
/// Authorizes division requirements. The division comes from an
/// <see cref="INhDivisionScopedResource"/> when the caller passes one, and otherwise from
/// the active-division request header. Without a request and without a scoped resource
/// the requirement is not met.
/// </summary>
public partial class ActiveDivisionAccessHandler<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim
    > : AuthorizationHandler<ActiveDivisionAccessRequirement>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
{
    protected readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceScopeFactory? _scopeFactory;

    public ActiveDivisionAccessHandler(IHttpContextAccessor httpContextAccessor)
        : this(httpContextAccessor, null)
    {
    }

    public ActiveDivisionAccessHandler(
        IHttpContextAccessor httpContextAccessor,
        IServiceScopeFactory? scopeFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _scopeFactory = scopeFactory;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ActiveDivisionAccessRequirement requirement)
    {
        if (context.User.HasClaim(NhPlatformClaimTypes.Permission, Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            context.Succeed(requirement);
            return;
        }

        var userIdString = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdString, out var userId))
        {
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        Guid? divisionId;
        if (context.Resource is INhDivisionScopedResource scopedResource)
        {
            divisionId = scopedResource.DivisionId;
        }
        else if (httpContext is not null)
        {
            divisionId = httpContext.Request.GetActiveDivisionId();
        }
        else
        {
            // No request and no resource that names a division: nothing to authorize against.
            return;
        }

        if (httpContext is not null)
        {
            await AuthorizeAsync(httpContext.RequestServices, context, requirement, userId, divisionId);
            return;
        }

        if (_scopeFactory is null)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        await AuthorizeAsync(scope.ServiceProvider, context, requirement, userId, divisionId);
    }

    private static async Task AuthorizeAsync(
        IServiceProvider services,
        AuthorizationHandlerContext context,
        ActiveDivisionAccessRequirement requirement,
        Guid userId,
        Guid? divisionId)
    {
        if (!divisionId.HasValue)
        {
            return;
        }

        // Division membership is read from the user store, not from token claims, so a
        // revoked membership takes effect immediately.
        var userManager = services.GetRequiredService<INhUserManager>();
        var userClaims = await userManager.GetValidClaimsByUserIdAsync(userId, true);
        if (await userManager.DivisionAccessAsync(
                divisionId,
                userClaims,
                requirement.RequiredClaims,
                requirement.RequiredRoles))
        {
            context.Succeed(requirement);
        }
    }
}
