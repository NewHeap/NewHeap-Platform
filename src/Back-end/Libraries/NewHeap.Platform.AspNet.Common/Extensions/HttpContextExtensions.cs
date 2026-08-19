using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common;

public static partial class HttpContextExtensions
{
    public static Guid? GetActiveDivisionId(this HttpRequest httpRequest)
    {
        var activeDivisionIdString = httpRequest.Headers
            .FirstOrDefault(x => x.Key.ToLower().Trim() == Constants.HttpHeaderKeys.ActiveDivisionId.ToLower().Trim())
            .Value.ToString();
        var activeDivisionIdFound = Guid.TryParse(activeDivisionIdString, out var activeDivisionId);

        return activeDivisionIdFound ? activeDivisionId : null;
    }

    public static Guid? GetActiveDivisionId(this HttpContext httpContext)
    {
        return httpContext?.Request?.GetActiveDivisionId();
    }

    public static Guid? GetUserId(this HttpContext httpContext)
    {
        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            if (Guid.TryParse(httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                return userId;
            }
        }

        return null;
    }

    public static async Task<bool> HasDivisionAccessAsync(
        this HttpContext httpContext,
        Guid? divisionId,
        IEnumerable<Claim>? requireClaims = null,
        IEnumerable<string>? requireRoles = null,
        CancellationToken cancellationToken = default)
    {
        if (httpContext.User.HasClaim(NhPlatformClaimTypes.Permission, Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            return true;
        }

        var userId = httpContext.GetUserId();

        if (!divisionId.HasValue || !userId.HasValue)
        {
            return false;
        }

        var userManager = httpContext.RequestServices.GetRequiredService<INhUserManager>();
        var userClaims = await userManager.GetValidClaimsByUserIdAsync(userId.Value, true, cancellationToken);

        if (await userManager.DivisionAccessAsync(
            divisionId, 
            userClaims,    
            requireClaims, 
            requireRoles, 
            cancellationToken))
        {
            return true;
        }

        return false;
    }
}