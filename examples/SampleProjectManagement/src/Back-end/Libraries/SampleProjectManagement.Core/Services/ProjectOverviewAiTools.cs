using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

public sealed record ProjectAiProjectInput(Guid ProjectId);

public sealed record ProjectAiProjectItem(
    Guid Id,
    string Key,
    string Name,
    ProjectStatus Status,
    DateTimeOffset? Deadline,
    Guid? OwnerUserId);

public sealed record ProjectAiOpenTaskItem(Guid Id, string Title);

public sealed record ProjectAiMemberItem(Guid UserId, string DisplayName, ProjectMemberRole Role);

/// <summary>
/// The reads of the project overview (SPM-372), always within the authorized active division.
/// </summary>
public interface IProjectAiOverviewReadService
{
    Task<ProjectAiProjectItem?> GetProjectForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectAiOpenTaskItem>> ListOpenTasksForAiAsync(
        Guid divisionId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectAiMemberItem>> ListMembersForAiAsync(
        Guid divisionId,
        Guid projectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SPM-372: three reads that the project overview composition chains into one tool: one project, its
/// open tasks and its members. Each is a governed tool of its own as well, which the project assistant
/// may call one by one; the composition saves the model the rounds in between.
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectOverviewAiTools(IProjectAiOverviewReadService overview)
{
    public const string GetToolId = "projects.get";
    public const string ListOpenTasksToolId = "projects.list-open-tasks";
    public const string ListMembersToolId = "projects.list-members";

    /// <summary>The stable code of a project that is not in the authorized division.</summary>
    public const string ProjectNotFoundCode = "project-not-found";

    /// <summary>The most open tasks one call lists.</summary>
    public const int MaxOpenTasks = 50;

    [NhAiTool(
        "get",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp | NhAiToolExposure.Agent,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Description("Read one project of the caller's authorized active division: its key, name, status, deadline and owner.")]
    public async Task<TaskResult<ProjectAiProjectItem>> GetAsync(
        ProjectAiProjectInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (DivisionOf(context) is not { } divisionId)
        {
            return TaskResult<ProjectAiProjectItem>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        var project = await overview.GetProjectForAiAsync(divisionId, input.ProjectId, cancellationToken);
        return project is null
            ? TaskResult<ProjectAiProjectItem>.Failed(ProjectNotFoundCode, "The project is not in the authorized division.")
            : TaskResult<ProjectAiProjectItem>.Succeeded(project);
    }

    [NhAiTool(
        "list-open-tasks",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp | NhAiToolExposure.Agent,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Description("List the open tasks of one project of the caller's authorized active division.")]
    public async Task<TaskResult<IReadOnlyList<ProjectAiOpenTaskItem>>> ListOpenTasksAsync(
        ProjectAiProjectInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (DivisionOf(context) is not { } divisionId)
        {
            return TaskResult<IReadOnlyList<ProjectAiOpenTaskItem>>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        return TaskResult<IReadOnlyList<ProjectAiOpenTaskItem>>.Succeeded(
            await overview.ListOpenTasksForAiAsync(divisionId, input.ProjectId, MaxOpenTasks, cancellationToken));
    }

    [NhAiTool(
        "list-members",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp | NhAiToolExposure.Agent,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Description("List the members of one project of the caller's authorized active division with their role.")]
    public async Task<TaskResult<IReadOnlyList<ProjectAiMemberItem>>> ListMembersAsync(
        ProjectAiProjectInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (DivisionOf(context) is not { } divisionId)
        {
            return TaskResult<IReadOnlyList<ProjectAiMemberItem>>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        return TaskResult<IReadOnlyList<ProjectAiMemberItem>>.Succeeded(
            await overview.ListMembersForAiAsync(divisionId, input.ProjectId, cancellationToken));
    }

    private static Guid? DivisionOf(NhAiInvocationContext context)
    {
        return context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var value) && Guid.TryParse(value, out var divisionId)
            ? divisionId
            : null;
    }
}
