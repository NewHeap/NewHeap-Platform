namespace SampleProjectManagement.Api.Authorization;

public static class SampleAuthorizationDefaults
{
    public const string Password = "Sample123!";

    public const string ManagerEmail = "sample@example.test";
    public const string ViewerEmail = "viewer@example.test";
    public const string DivisionEditorEmail = "division-editor@example.test";
    public const string ProjectEditorEmail = "project-editor@example.test";

    public const string ManagerRole = "sample-project-manager";
    public const string ViewerRole = "sample-project-viewer";
    public const string DivisionEditorRole = "sample-division-editor";
    public const string ProjectMemberRole = "sample-project-member";

    public static readonly Guid NorthDivisionId =
        Guid.Parse("b14a1178-8bd7-4e87-845f-e0d89b63f099");

    public static readonly Guid SouthDivisionId =
        Guid.Parse("74e0420a-186b-4b4b-af08-91b4379fba2c");

    public static readonly Guid AlphaProjectId =
        Guid.Parse("87534f33-bd4b-43ed-8ce5-8861d320271d");

    public static readonly Guid BetaProjectId =
        Guid.Parse("b926cbb0-7dc6-4504-8bcc-384ee787d642");
}

public static class SampleProjectClaimTypes
{
    public const string ProjectRole = "sample.project.role";
    public const string ProjectPermission = "sample.project.permission";

    public static string PermissionValue(Guid projectId, string permission) =>
        $"{projectId}_{permission}";

    public static string RoleValue(Guid projectId, string role) =>
        $"{projectId}_{role}";
}

public static class SampleAuthorizationPolicies
{
    public const string ProjectConfidentialView =
        "app.active-division.any-project.project.confidential.view";
}
