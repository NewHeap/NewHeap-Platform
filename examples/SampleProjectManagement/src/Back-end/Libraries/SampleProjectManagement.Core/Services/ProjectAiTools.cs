using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.AI;

namespace SampleProjectManagement.Core.Services;

[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectAiTools(
    IProjectAiReadService projectReadService,
    IProjectAiMutationService projectMutationService)
{
    public const string DivisionScopeKey = "division-id";
    public const string ReadCapability = "projects-read";
    public const string ManageCapability = "projects-manage";

    [NhAiTool(
        "search",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp | NhAiToolExposure.Agent,
        RequiredCapabilities = new[] { ReadCapability })]
    [Description("Search projects that belong to the caller's authorized active division.")]
    public async Task<TaskResult<IReadOnlyList<ProjectAiSearchItem>>> SearchAsync(
        ProjectAiSearchInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return TaskResult<IReadOnlyList<ProjectAiSearchItem>>.Failed(
                "The authorized AI invocation context has no active division.");
        }

        var projects = await projectReadService.SearchForAiAsync(
            divisionId,
            input.Query,
            input.Limit,
            cancellationToken);

        return TaskResult<IReadOnlyList<ProjectAiSearchItem>>.Succeeded(projects);
    }

    [NhAiTool(
        "change-status",
        1,
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent,
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        VerifierId = ProjectAiStatusVerifier.VerifierId,
        RequiredCapabilities = new[] { ManageCapability })]
    [Description("Change one project status in the authorized active division after bound approval.")]
    public Task<TaskResult<ProjectAiStatusChangeReport>> ChangeStatusAsync(
        ProjectAiStatusChangeInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return Task.FromResult(
                TaskResult<ProjectAiStatusChangeReport>.Failed(
                    "The authorized AI invocation context has no active division."));
        }

        return projectMutationService.ChangeStatusForAiAsync(
            divisionId,
            input.ProjectId,
            input.Status,
            cancellationToken);
    }
}
