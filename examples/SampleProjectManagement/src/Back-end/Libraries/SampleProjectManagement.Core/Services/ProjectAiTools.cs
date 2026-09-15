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
    IProjectAiMutationService projectMutationService,
    IProjectAiStatusReceiptService statusReceiptService)
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

    // The application owns approval and idempotency for this write: it validates its own
    // single-use grant, burns it on any mismatch, and reconciles replays of the same key.
    // The shared invoker still authorizes, resolves capabilities, reserves budget, bounds
    // execution and audits the consumer-attested steps. The flat export schema keeps the
    // domain wire contract for remote MCP callers.
    [NhAiTool(
        "apply-status-receipt",
        1,
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        ExportSchema = NhAiToolExportSchema.Flat,
        RequiredCapabilities = new[] { ManageCapability })]
    [NhAiToolExportName("projects.apply-status-receipt")]
    [Description("Apply one approved project status change in the authorized active division and return the domain receipt; an invalid, expired or replayed approval grant returns a deny receipt.")]
    public async Task<TaskResult<ProjectAiStatusReceipt>> ApplyStatusReceiptAsync(
        ProjectAiStatusReceiptRequest input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        if (!context.TryGetScopeValue(DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return TaskResult<ProjectAiStatusReceipt>.Failed(
                "The authorized AI invocation context has no active division.");
        }

        var receipt = await statusReceiptService.ApplyAsync(
            divisionId,
            input,
            cancellationToken);
        if (string.Equals(receipt.Execution, ProjectAiStatusReceipt.DenyExecution, StringComparison.Ordinal))
        {
            // An expected denial is a failed result that still carries the typed receipt.
            return TaskResult<ProjectAiStatusReceipt>
                .Failed(receipt.Code, "The project status change was not executed.")
                .WithData(receipt);
        }

        return TaskResult<ProjectAiStatusReceipt>.Succeeded(receipt);
    }
}
