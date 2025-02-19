using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AspNet.Policy.Requirements;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Extensions;

public static partial class ServiceCollectionExtensions
{
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