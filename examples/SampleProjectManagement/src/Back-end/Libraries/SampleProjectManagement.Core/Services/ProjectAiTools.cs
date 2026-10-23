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
    public const string ChangeStatusToolId = "projects.change-status";

    /// <summary>
    /// The stable code of a call whose authorized context has no active division. The model
    /// receives it as <c>{"success":false,"code":"project-division-scope-missing","message":...}</c>.
    /// </summary>
    public const string DivisionScopeMissingCode = "project-division-scope-missing";

    /// <summary>
    /// The stable code of a search whose query is longer than <see cref="MaxSearchQueryLength"/>.
    /// Its message keeps its format and arguments, which the MCP export publishes next to the text.
    /// </summary>
    public const string SearchQueryTooLongCode = "project-search-query-too-long";

    /// <summary>
    /// The longest query, in characters, that <c>projects.search</c> accepts.
    /// </summary>
    public const int MaxSearchQueryLength = 200;

    /// <summary>
    /// The policy a person needs to change a project status, and so to approve an agent's status
    /// proposal. The API registers it as active-division access with the manage permission.
    /// </summary>
    public const string ManagePolicy = "app.active-division.project.manage";

    /// <summary>
    /// The policy a person needs to see projects of the active division.
    /// </summary>
    public const string ViewPolicy = "app.active-division.project.view";
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
                DivisionScopeMissingCode,
                "The authorized AI invocation context has no active division.");
        }
        if (input.Query is { Length: > MaxSearchQueryLength } query)
        {
            // Declared as a FormattableString, the message keeps its format and arguments: an MCP
            // client receives "A project search query has at most {0} characters; this one has {1}."
            // with both numbers next to the text and can translate it. An interpolated string passed
            // straight to Failed would bind to its string overload and stay a text of its own.
            FormattableString tooLong =
                $"A project search query has at most {MaxSearchQueryLength} characters; this one has {query.Length}.";
            return TaskResult<IReadOnlyList<ProjectAiSearchItem>>.Failed(SearchQueryTooLongCode, tooLong);
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
        // The intake reviewer may propose status changes for projects nobody asked it about.
        // The agent registration grants the tool as well; every proposal still waits for a
        // project approver who satisfies ManagePolicy.
        AllowAgentGrant = true,
        RequiredCapabilities = new[] { ManageCapability })]
    [Authorize(Policy = ManagePolicy)]
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
                    DivisionScopeMissingCode,
                    "The authorized AI invocation context has no active division."));
        }

        // SPM-308: the status event names the agent and the subject it works in, so a trigger
        // can skip the agent's own change on that subject.
        return projectMutationService.ChangeStatusForAiAsync(
            divisionId,
            input.ProjectId,
            input.Status,
            ProjectAgentCause.Of(context),
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
                DivisionScopeMissingCode,
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
                DivisionScopeMissingCode,
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
