using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Controllers;

/// <summary>
/// SPM-332: the messaging app in which a person continues a chat with the project assistant. The
/// signed-in person links one of their chats in the portal; the messaging provider then delivers the
/// person's messages and their answers to approval cards with the chat's link token, so those calls
/// carry no sign-in. <see cref="SampleAssistantMessagingChannel"/> runs each turn as the person through
/// the platform's <c>INhAssistantConversationTurns</c>.
/// </summary>
[ApiController]
[Route("assistant-channel")]
public sealed class AssistantChannelController(SampleAssistantMessagingChannel channel) : ControllerBase
{
    [HttpPost("links")]
    [Authorize(Policy = SampleAssistantComposition.AccessPolicy)]
    [EndpointSummary("Link a chat to the messaging app")]
    [EndpointDescription("Issues the link token with which the messaging provider delivers the signed-in person's messages to this chat, in the person's active division. Whether the person may use the chat is checked on every delivery, so a link to someone else's chat finds nothing. 400 without an active division, 401 without a sign-in, 403 without the assistant's access policy.")]
    [ProducesResponseType<AssistantChannelLinkViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Link([FromBody] AssistantChannelLinkMutateModel model)
    {
        if (HttpContext.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        if (HttpContext.GetActiveDivisionId() is not { } divisionId)
        {
            return BadRequest(new AssistantChannelErrorViewModel(SampleAssistantMessagingChannel.DeliveryInvalidCode));
        }

        var link = channel.Link(model.ConversationId, userId, divisionId);
        return Ok(new AssistantChannelLinkViewModel(link.Token, link.ExpiresAt));
    }

    [HttpPost("messages")]
    [AllowAnonymous]
    [EndpointSummary("Send a person's message from the messaging app")]
    [EndpointDescription("The messaging provider delivers a person's message with the link token of their chat. The turn runs as the person the token names, with the checks of POST /api/assistant/conversations/{id}/messages and the person's own rights for every tool, and answers with the assistant's text and, when the turn waits for the person, the approval card to answer through POST assistant-channel/decisions. The turn keeps running when the delivery gives up; the chat in the portal holds its result. 400 for an unknown, altered or expired token or an invalid message, 403 when the person may not use the assistant or no longer sign in, 404 for a chat the person does not take part in, 409 when the chat is busy or the delivery repeats one.")]
    [ProducesResponseType<AssistantChannelAnswerViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Send([FromBody] AssistantChannelMessageMutateModel model, CancellationToken cancellationToken)
    {
        var sent = await channel.SendAsync(
            new SampleAssistantChannelMessage(model.DeliveryId, model.Token, model.Text),
            cancellationToken);
        return sent.Success ? Ok(ToViewModel(sent.Data!)) : Refusal(sent);
    }

    [HttpPost("decisions")]
    [AllowAnonymous]
    [EndpointSummary("Answer an approval card from the messaging app")]
    [EndpointDescription("The messaging provider delivers the person's answer to an approval card with the chat's link token: yes or approve approves, no or reject rejects, bound to the card's proposal hash, with the checks of POST /api/assistant/conversations/{id}/approvals/{approvalId}/decide. Answers with the resumed turn's result. 400 for an unknown, altered or expired token or another text, 403 when the person may not decide it or no longer sign in, 404 for an unknown approval or chat, 409 when the proposal changed, was decided or expired.")]
    [ProducesResponseType<AssistantChannelAnswerViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide([FromBody] AssistantChannelDecisionMutateModel model, CancellationToken cancellationToken)
    {
        var decided = await channel.DecideAsync(
            new SampleAssistantChannelDecision(model.Token, model.ApprovalId, model.ProposalHash, model.Text),
            cancellationToken);
        return decided.Success ? Ok(ToViewModel(decided.Data!)) : Refusal(decided);
    }

    private static AssistantChannelAnswerViewModel ToViewModel(SampleAssistantChannelAnswer answer)
    {
        return new AssistantChannelAnswerViewModel(
            answer.Status,
            answer.Code,
            answer.Text,
            answer.Approval is { } approval
                ? new AssistantChannelApprovalViewModel(approval.ApprovalId, approval.ProposalHash, approval.Summary)
                : null);
    }

    private IActionResult Refusal(TaskResult result)
    {
        var code = result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? SampleAssistantMessagingChannel.DeliveryInvalidCode;
        var error = new AssistantChannelErrorViewModel(code);
        return code switch
        {
            SampleAssistantMessagingChannel.PersonUnavailableCode
                or NhAssistantErrorCodes.AgentForbidden
                or NhAssistantErrorCodes.ContextUnavailable
                or NhAssistantErrorCodes.ApprovalForbidden
                or NhAssistantErrorCodes.ApprovalPolicyDenied => StatusCode(StatusCodes.Status403Forbidden, error),
            NhAssistantErrorCodes.ConversationNotFound
                or NhAssistantErrorCodes.AgentNotFound
                or NhAssistantErrorCodes.ApprovalNotFound => NotFound(error),
            NhAssistantErrorCodes.ConversationBusy
                or NhAssistantErrorCodes.MessageDuplicate
                or NhAssistantErrorCodes.ApprovalNotPending
                or NhAssistantErrorCodes.ProposalHashMismatch
                or NhAssistantErrorCodes.ProposalStale => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
