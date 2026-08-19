using Microsoft.AspNetCore.Authorization;

namespace SampleProjectManagement.Api.Authorization;

public static class AuthorizationPolicyBuilderExtensions
{
    public static AuthorizationPolicyBuilder RequireAnyProjectActiveDivisionAccess(
        this AuthorizationPolicyBuilder policy,
        string permission,
        string projectRouteValueName = "projectId")
    {
        policy.Requirements.Add(new ProjectAccessRequirement(
            permission,
            projectRouteValueName));

        return policy;
    }
}
