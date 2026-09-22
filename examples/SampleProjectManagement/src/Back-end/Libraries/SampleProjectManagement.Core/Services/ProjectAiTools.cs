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
    public const string ApplyStatusReceiptToolId = "projects.apply-status-receipt";
    public const string IssueStatusApprovalToolId = "projects.issue-status-approval";
    public static readonly TimeSpan StatusApprovalLifetime = TimeSpan.FromMinutes(5);

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

    // Issues the single-use grant that apply-status-receipt consumes. Issuing an approval runs
    // under the application's own manage authorization, so the tool declares the Issuer role
    // instead of consuming approval; the shared invoker still authorizes, resolves
    // capabilities, reserves budget, bounds execution and audits the approval code "issuer".
    [NhAiTool(
        "issue-status-approval",
        1,
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.Issuer,
        ExportSchema = NhAiToolExportSchema.Flat,
        RequiredCapabilities = new[] { ManageCapability })]
    [NhAiToolExportName(IssueStatusApprovalToolId)]
    [Description("Issue a single-use, short-lived approval grant for one project status change in the authorized active division.")]
    public Task<TaskResult<ProjectAiStatusApprovalGrant>> IssueStatusApprovalAsync(
        ProjectAiStatusApprovalRequest input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.TryGetScopeValue(DivisionScopeKey, out var divisionValue)
            || !Guid.TryParse(divisionValue, out var divisionId))
        {
            return Task.FromResult(TaskResult<ProjectAiStatusApprovalGrant>.Failed(
                "project-division-scope-missing",
                "The authorized AI invocation context has no active division."));
        }

        var grant = statusReceiptService.IssueApprovalGrant(
            divisionId,
            input.ProjectId,
            input.Status,
            DateTimeOffset.UtcNow.Add(StatusApprovalLifetime));
        return Task.FromResult(TaskResult<ProjectAiStatusApprovalGrant>.Succeeded(grant));
    }

    // The application owns approval and idempotency for this write. Its evidence validator
    // burns the presented grant and returns an invalid, expired or replayed grant as the typed
    // deny receipt; a replayed key reaches the engine, which returns the reconciled receipt.
    // The shared invoker still authorizes, resolves capabilities, reserves budget, bounds
    // execution and audits. The flat export keeps the domain wire contract, and the explicit
    // destructive hint tells MCP clients to confirm this state change even though its governance
    // effect is an idempotent mutation.
    [NhAiTool(
        "apply-status-receipt",
        1,
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Mcp,
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        ExportSchema = NhAiToolExportSchema.Flat,
        DestructiveHint = NhAiToolHint.True,
        RequiredCapabilities = new[] { ManageCapability })]
    [NhAiToolExportName(ApplyStatusReceiptToolId)]
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
