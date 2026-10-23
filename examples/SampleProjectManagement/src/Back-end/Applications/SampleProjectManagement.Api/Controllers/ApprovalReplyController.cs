using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-268: the webhook of the messaging provider that delivers approvers' replies to messages about
/// waiting agent proposals. The reply token the application issued to the person says who answers which
/// proposal, so the call itself carries no sign-in; <see cref="SampleApprovalReplyChannel"/> decides as
/// that person through the platform's proposal decision service.
/// </summary>
[ApiController]
[Route("approval-replies")]
[AllowAnonymous]
public sealed class ApprovalReplyController(SampleApprovalReplyChannel channel) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EndpointSummary("Decide an agent proposal with a messaging reply")]
    [EndpointDescription("The messaging provider delivers an approver's reply with the reply token of the message it answers: yes or approve approves, no or reject rejects, as the person the token names and with the checks of POST /api/assistant/approvals/{approvalId}/decisions. A delivery repeated with the same answer returns the earlier decision with duplicate true. 400 for an unknown, altered or expired token or another text, 403 when the person may not decide (for example assistant-four-eyes-required) or no longer sign in, 404 for a proposal the person may not see, 409 when the proposal changed, expired or was decided otherwise.")]
    [ProducesResponseType<ApprovalReplyViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApprovalReplyErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApprovalReplyErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ApprovalReplyErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApprovalReplyErrorViewModel>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Post([FromBody] ApprovalReplyMutateModel model, CancellationToken cancellationToken)
    {
        var decided = await channel.ReceiveAsync(
            new SampleApprovalReply(model.DeliveryId, model.Token, model.Text),
            cancellationToken);
        if (decided.Success)
        {
            var result = decided.Data!;
            return Ok(new ApprovalReplyViewModel(
                result.ApprovalId,
                result.Status,
                result.Decision == NhAssistantProposalDecision.Approve ? "approve" : "reject",
                result.Duplicate));
        }

        var code = decided.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? SampleApprovalReplyChannel.ReplyInvalidCode;
        var error = new ApprovalReplyErrorViewModel(code);
        return code switch
        {
            SampleApprovalReplyChannel.ReplyInvalidCode or NhAssistantErrorCodes.ApprovalDecisionInvalid => BadRequest(error),
            SampleApprovalReplyChannel.PersonUnavailableCode
                or NhAssistantErrorCodes.FourEyesRequired
                or NhAssistantErrorCodes.ApprovalPolicyDenied
                or NhAssistantErrorCodes.ConsultAskerDecides => StatusCode(StatusCodes.Status403Forbidden, error),
            NhAssistantErrorCodes.ApprovalNotFound => NotFound(error),
            _ => Conflict(error)
        };
    }
}
