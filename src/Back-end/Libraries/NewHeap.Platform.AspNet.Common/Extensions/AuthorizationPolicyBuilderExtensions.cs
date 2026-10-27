using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AspNet.Policy.Requirements;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common;

public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Requires membership of the active division (or of the division an
    /// <c>INhDivisionScopedResource</c> names), with every given division role and claim in it.
    /// Without roles and claims, membership alone decides; the division access-all permission
    /// grants every division.
    /// </summary>
    public static AuthorizationPolicyBuilder RequireActiveDivisionAccess(
        this AuthorizationPolicyBuilder authorizationPolicyBuilder, IEnumerable<string>? roles = null,
        params Claim[] claims)
    {
        authorizationPolicyBuilder.Requirements.Add(new ActiveDivisionAccessRequirement(
            requiredRoles: roles,
            requiredClaims: claims));

        return authorizationPolicyBuilder;
    }
}