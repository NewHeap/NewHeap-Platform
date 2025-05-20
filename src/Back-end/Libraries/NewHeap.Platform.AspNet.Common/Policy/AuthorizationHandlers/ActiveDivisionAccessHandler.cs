using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy.Requirements;
using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;

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

    public ActiveDivisionAccessHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
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

        var httpContext = _httpContextAccessor.HttpContext;
        var activeDivisionId = httpContext?.Request.GetActiveDivisionId();
        var userIdString = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var parseSucces = Guid.TryParse(userIdString, out var userId);

        if (parseSucces)
        {
            if (await httpContext!.HasDivisionAccessAsync(
                activeDivisionId,
                requirement.RequiredClaims,
                requirement.RequiredRoles
            ))
            {
                context.Succeed(requirement);
            }
        }
    }
}