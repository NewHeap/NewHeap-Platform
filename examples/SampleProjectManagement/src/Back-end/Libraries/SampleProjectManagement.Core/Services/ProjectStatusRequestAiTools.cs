using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Core.Services;

/// <param name="ProjectId">The project whose status the person asks for.</param>
/// <param name="Status">The status the person asks for.</param>
/// <param name="Reason">Why, in the person's own words; approvers read it.</param>
/// <param name="RequestId">The client's id of this request, so a request sent twice records one proposal.</param>
public sealed record ProjectStatusRequestInput(Guid ProjectId, ProjectStatus Status, string? Reason, string RequestId);

/// <summary>
/// What a status request recorded: the proposal that waits for an approver, its outcome
/// (<c>created</c>, <c>already-open</c>, <c>recently-rejected</c> or <c>not-needed</c>) and status.
/// </summary>
public sealed record ProjectStatusRequestReceipt(Guid? ApprovalId, string Outcome, string? Status);

/// <summary>
/// Records a person's request for another project status as the intake reviewer's proposal for that
/// person; the API implements it over the platform's <c>INhAssistantProposals.ProposeForAsync</c>.
/// </summary>
public interface IProjectStatusRequests
{
    Task<TaskResult<ProjectStatusRequestReceipt>> RequestAsync(
        ProjectStatusRequestInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// SPM-364 and SPM-370: the project page's status request as a tool of the API's MCP exposure. A person's
/// own AI app asks for another status; the call itself changes no project and needs no approval, but the
/// proposal it records waits for a second person. With <c>NewHeap:AI:Assistant:Mcp:ApprovalLinkTemplate</c>
/// the platform sends an app that supports it to the portal's page of that proposal and answers the app's
/// retry with the decision; the app's own confirmation never approves the change.
/// </summary>
[Authorize]
[NhAiToolSet("projects")]
public sealed class ProjectStatusRequestAiTools(IProjectStatusRequests requests)
{
    public const string RequestStatusToolId = "projects.request-status";

    /// <summary>The name an MCP client calls the tool by.</summary>
    public const string RequestStatusToolName = "projects_request_status_v1";

    [NhAiTool(
        "request-status",
        1,
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Mcp,
        // The call records a request; the status change is the proposal, which a second person approves.
        Approval = NhAiApprovalRequirement.NotRequired,
        // The request id names one proposal per person, as on the project page.
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        RequiredCapabilities = new[] { ProjectAiTools.ReadCapability })]
    [Authorize(Policy = ProjectAiTools.ViewPolicy)]
    [Description("Ask for another status of a project of the authorized active division. Nothing changes yet: the request becomes a proposal that another approver of the project decides in the portal.")]
    public Task<TaskResult<ProjectStatusRequestReceipt>> RequestStatusAsync(
        ProjectStatusRequestInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return requests.RequestAsync(input, context, cancellationToken);
    }
}
