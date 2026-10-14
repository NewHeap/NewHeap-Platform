using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.Mutate;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Core.Services;

public sealed record ProjectTaskCompleteInput(Guid TaskId);

public sealed record ProjectTaskCompletion(Guid TaskId, Guid ProjectId, bool WasOpen);

/// <summary>
/// SPM-282: lets the intake reviewer propose to complete a project task. Completing a task that is
/// already done changes nothing, so the tool is idempotent; still, people often tick tasks off by
/// hand while the proposal waits, and the task guard then closes the proposal as already done.
/// </summary>
[Authorize]
[NhAiToolSet("project-tasks")]
public sealed class ProjectTaskAiTools(SampleProjectManagementDbContext dbContext, ProjectTaskService tasks)
{
    public const string CompleteToolId = "project-tasks.complete";

    [NhAiTool(
        "complete",
        1,
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        AllowAgentGrant = true,
        RequiredCapabilities = new[] { ProjectAiTools.ManageCapability })]
    [Authorize(Policy = ProjectAiTools.ManagePolicy)]
    [Description("Mark one task of a project in the authorized active division as completed.")]
    public async Task<TaskResult<ProjectTaskCompletion>> CompleteAsync(
        ProjectTaskCompleteInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(ProjectAiTools.DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return TaskResult<ProjectTaskCompletion>.Failed(
                ProjectAiTools.DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }

        var task = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(item => item.Id == input.TaskId && item.Project.DivisionId == divisionId)
            .Select(item => new { item.Id, item.ProjectId, item.Title, item.IsCompleted })
            .SingleOrDefaultAsync(cancellationToken);
        if (task is null)
        {
            return TaskResult<ProjectTaskCompletion>.Failed("The task is unavailable in the authorized division scope.");
        }
        if (task.IsCompleted)
        {
            return TaskResult<ProjectTaskCompletion>.Succeeded(new ProjectTaskCompletion(task.Id, task.ProjectId, false));
        }

        // An agent completes the task through an approved proposal, so no person is recorded.
        var updated = await tasks.UpdateAsync(
            task.Id,
            new ProjectTaskMutateModel { ProjectId = task.ProjectId, Title = task.Title, IsCompleted = true },
            cancellationToken: cancellationToken);
        return updated.Success
            ? TaskResult<ProjectTaskCompletion>.Succeeded(new ProjectTaskCompletion(task.Id, task.ProjectId, true))
            : TaskResult<ProjectTaskCompletion>.Failed(updated);
    }
}
