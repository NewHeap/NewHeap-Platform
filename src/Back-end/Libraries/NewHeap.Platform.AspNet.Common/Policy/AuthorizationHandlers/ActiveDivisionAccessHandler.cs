using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Policy.Requirements;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Policy.AuthorizationHandlers;

public partial class ActiveDivisionAccessHandler : AuthorizationHandler<ActiveDivisionAccessRequirement>
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
        if (context.User.HasClaim(NhPlatformClaimTypes.Permission, Constants.DivisionPermissionClaimValues.AccessAll))
        {
            context.Succeed(requirement);
            return;
        }

        var httpContext = _httpContextAccessor.HttpContext;
        var userManager = httpContext.RequestServices.GetService(typeof(NhUserManager)) as NhUserManager;
        var activeDivisionId = httpContext?.Request.GetActiveDivisionId();

        var userIdString = context.User?.FindFirstValue(ClaimTypes.NameIdentifier);

        var parseSucces = Guid.TryParse(userIdString, out var userId);

        if (parseSucces)
        {
            var user = await userManager.FindByIdAsync(userId.ToString());
            List<Claim>? userClaims = await userManager.GetValidClaims(user, true);
            if (userManager != null && await userManager.DivisionAccessAsync(activeDivisionId, userClaims,
                    requirement.RequiredClaims, requirement.RequiredRoles))
            {
                context.Succeed(requirement);
            }
        }
    }
}