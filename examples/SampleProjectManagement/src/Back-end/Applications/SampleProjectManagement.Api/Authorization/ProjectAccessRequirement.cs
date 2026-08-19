using Microsoft.AspNetCore.Authorization;

namespace SampleProjectManagement.Api.Authorization;

/// <summary>
/// Application-specific resource permission. Access may be granted at
/// application, active-division or individual-project level.
/// </summary>
public sealed class ProjectAccessRequirement : IAuthorizationRequirement
{
    public ProjectAccessRequirement(
        string permission,
        string projectRouteValueName = "projectId")
    {
        Permission = permission;
        ProjectRouteValueName = projectRouteValueName;
    }

    public string Permission { get; }

    public string ProjectRouteValueName { get; }

    public string SystemPermission => $"app.project.{Permission}";

    public string DivisionPermission => $"project.{Permission}";
}
