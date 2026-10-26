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
/// the platform's <c>INhAssistantConversationTurns</c>. SPM-341: the person also starts a chat, reads the
/// linked chat, answers its question card and stops its turn from the messaging app, through the
/// platform's <c>INhAssistantConversations</c> and <c>AnswerAsync</c>.
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

    [HttpPost("chats")]
    [AllowAnonymous]
    [EndpointSummary("Start a chat from the messaging app")]
    [EndpointDescription("SPM-341: the messaging provider delivers the person's request for a new chat with an agent, with a link token of one of the person's chats. The platform's INhAssistantConversations starts the chat as the person the token names, with the checks of POST /api/assistant/conversations, and the answer carries the link token of the new chat, which the messaging app uses from then on. 400 for an unknown, altered or expired token or a title longer than 200 characters, 403 when the person may not use the assistant or the agent or no longer sign in, 404 for an unknown agent or one that is not offered in the chat.")]
    [ProducesResponseType<AssistantChannelChatViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> StartChat([FromBody] AssistantChannelChatMutateModel model, CancellationToken cancellationToken)
    {
        var started = await channel.StartChatAsync(
            new SampleAssistantChannelStart(model.Token, model.AgentId, model.Title),
            cancellationToken);
        if (!started.Success)
        {
            return Refusal(started);
        }

        var chat = started.Data!;
        return Ok(new AssistantChannelChatViewModel(chat.Token, chat.ExpiresAt, ToViewModel(chat.Chat)));
    }

    [HttpPost("chat")]
    [AllowAnonymous]
    [EndpointSummary("Read the linked chat from the messaging app")]
    [EndpointDescription("SPM-341: the messaging app shows the linked chat after a pause: its status, its title, the assistant's last answer and the approval or question card it waits for, read as the person the link token names with the checks of GET /api/assistant/conversations/{id}. The token travels in the body, never in the address. 400 for an unknown, altered or expired token, 403 when the person may not use the assistant or no longer sign in, 404 for a chat the person does not take part in.")]
    [ProducesResponseType<AssistantChannelChatStateViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Read([FromBody] AssistantChannelTokenMutateModel model, CancellationToken cancellationToken)
    {
        var read = await channel.ReadAsync(model.Token, cancellationToken);
        return read.Success ? Ok(ToViewModel(read.Data!)) : Refusal(read);
    }

    [HttpPost("answers")]
    [AllowAnonymous]
    [EndpointSummary("Answer a question card from the messaging app")]
    [EndpointDescription("SPM-341: the messaging provider delivers the person's reply to a question card with the chat's link token: the number of a choice as the card shows them, from 1, or the choice's text. The platform's INhAssistantConversationTurns.AnswerAsync answers as the person the token names, with the checks of POST /api/assistant/conversations/{id}/questions/{questionId}/answer, and the turn continues with that person's rights. Answers with the resumed turn's result. 400 for an unknown, altered or expired token or a reply that names no choice, 403 when the question asks another person or the person may not use the assistant or no longer sign in, 404 for an unknown question or chat, 409 when the question was answered, stopped or expired.")]
    [ProducesResponseType<AssistantChannelAnswerViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Answer([FromBody] AssistantChannelQuestionAnswerMutateModel model, CancellationToken cancellationToken)
    {
        var answered = await channel.AnswerAsync(
            new SampleAssistantChannelQuestionAnswer(model.Token, model.QuestionId, model.Text),
            cancellationToken);
        return answered.Success ? Ok(ToViewModel(answered.Data!)) : Refusal(answered);
    }

    [HttpPost("stops")]
    [AllowAnonymous]
    [EndpointSummary("Stop the linked chat's turn from the messaging app")]
    [EndpointDescription("SPM-341: the person stops the linked chat's turn from the messaging app, as the portal's stop button does with POST /api/assistant/conversations/{id}/cancel: a running turn is cancelled, a waiting approval rejected and a waiting question cancelled; a chat without a turn stays as it is. 400 for an unknown, altered or expired token, 403 when the person neither owns the chat nor started its turn, may not use the assistant or no longer sign in, 404 for a chat the person does not take part in.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stop([FromBody] AssistantChannelTokenMutateModel model, CancellationToken cancellationToken)
    {
        var stopped = await channel.StopAsync(model.Token, cancellationToken);
        return stopped.Success ? NoContent() : Refusal(stopped);
    }

    private static AssistantChannelAnswerViewModel ToViewModel(SampleAssistantChannelAnswer answer)
    {
        return new AssistantChannelAnswerViewModel(
            answer.Status,
            answer.Code,
            answer.Text,
            ToViewModel(answer.Approval),
            ToViewModel(answer.Question));
    }

    private static AssistantChannelChatStateViewModel ToViewModel(SampleAssistantChannelState chat)
    {
        return new AssistantChannelChatStateViewModel(
            chat.Status,
            chat.Title,
            chat.LastAnswer,
            ToViewModel(chat.Approval),
            ToViewModel(chat.Question));
    }

    private static AssistantChannelApprovalViewModel? ToViewModel(SampleAssistantChannelApproval? approval)
    {
        return approval is null
            ? null
            : new AssistantChannelApprovalViewModel(approval.ApprovalId, approval.ProposalHash, approval.Summary);
    }

    private static AssistantChannelQuestionViewModel? ToViewModel(SampleAssistantChannelQuestion? question)
    {
        return question is null
            ? null
            : new AssistantChannelQuestionViewModel(question.QuestionId, question.Question, question.Choices);
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
                or NhAssistantErrorCodes.ApprovalPolicyDenied
                or NhAssistantErrorCodes.QuestionForbidden
                or NhAssistantErrorCodes.TurnForbidden => StatusCode(StatusCodes.Status403Forbidden, error),
            NhAssistantErrorCodes.ConversationNotFound
                or NhAssistantErrorCodes.AgentNotFound
                or NhAssistantErrorCodes.ApprovalNotFound
                or NhAssistantErrorCodes.QuestionNotFound => NotFound(error),
            NhAssistantErrorCodes.ConversationBusy
                or NhAssistantErrorCodes.MessageDuplicate
                or NhAssistantErrorCodes.ApprovalNotPending
                or NhAssistantErrorCodes.ProposalHashMismatch
                or NhAssistantErrorCodes.ProposalStale
                or NhAssistantErrorCodes.QuestionNotPending
                or NhAssistantErrorCodes.QuestionExpired => Conflict(error),
            _ => BadRequest(error)
        };
    }
}
