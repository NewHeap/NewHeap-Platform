using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-364: the project page's request for another project status. The signed-in person asks; the API
/// records it as the intake reviewer's deferred proposal for that person, without a run, through
/// <see cref="SampleProjectStatusRequests"/>. Like the assistant's endpoints it needs the assistant's access
/// policy; the platform decides whether the person takes part in the project and may change its status, and a
/// second project approver decides the proposal.
/// </summary>
[ApiController]
[Route("projects/{projectId:guid}/status-requests")]
[Authorize(Policy = SampleAssistantComposition.AccessPolicy)]
public sealed class ProjectStatusRequestController(SampleProjectStatusRequests requests) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = SampleAssistantComposition.AccessPolicy)]
    [EndpointSummary("Ask for another project status")]
    [EndpointDescription("Records the request of the signed-in person as the intake reviewer's deferred proposal for that person, without a run, with the texts the approvers read in English and Dutch. It waits in the reviewer's thread at the project under the intake policy: the person who asked may not approve it, and a second project approver decides it, after which it executes with the rights of the person who asked. The same requestId returns the same proposal with repeated true; the same change that waits already answers already-open. 400 for an invalid body or arguments the tool refuses, 403 when the person does not take part in the project or may not change its status, 404 for an unknown project, 409 when the requestId recorded another change or the agent takes no proposals now.")]
    [ProducesResponseType<ProjectStatusRequestViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ModelStateResponseType>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProjectStatusRequestErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProjectStatusRequestErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProjectStatusRequestErrorViewModel>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Post(
        [FromRoute] Guid projectId,
        [FromBody] ProjectStatusRequestMutateModel model,
        CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var requested = await requests.RequestAsync(
            User,
            userId,
            projectId,
            model.Status,
            model.Reason,
            model.RequestId,
            CultureInfo.CurrentUICulture,
            cancellationToken);
        if (requested.Success)
        {
            var result = requested.Data!;
            return Ok(new ProjectStatusRequestViewModel(
                OutcomeOf(result.Outcome),
                result.ApprovalId,
                result.ConversationId,
                result.Status,
                result.PolicyId,
                result.FourEyes,
                result.ExpiresAt,
                result.Repeated));
        }

        var code = requested.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? NhAssistantErrorCodes.ProposalRefused;
        var error = new ProjectStatusRequestErrorViewModel(code);
        return code switch
        {
            NhAiToolFailureCodes.InputInvalid
                or NhAssistantErrorCodes.ProposalPresentationInvalid => BadRequest(error),
            NhAssistantErrorCodes.SubjectAccessDenied
                or NhAssistantErrorCodes.AgentForbidden
                or NhAssistantErrorCodes.ContextUnavailable
                or NhAiAgentFailureCodes.NotGranted
                or NhAiToolFailureCodes.CapabilityDenied => StatusCode(StatusCodes.Status403Forbidden, error),
            NhAssistantErrorCodes.SubjectNotFound => NotFound(error),
            _ => Conflict(error)
        };
    }

    private static string OutcomeOf(NhAssistantProposeOutcome outcome)
    {
        return outcome switch
        {
            NhAssistantProposeOutcome.AlreadyOpen => "already-open",
            NhAssistantProposeOutcome.RecentlyRejected => "recently-rejected",
            NhAssistantProposeOutcome.NotNeeded => "not-needed",
            _ => "created"
        };
    }
}
