using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
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
/// platform's <c>INhAssistantConversations</c> and <c>AnswerAsync</c>. SPM-358: the messaging app lists
/// the person's chats and agents and uploads attachments to the linked chat. SPM-380: the provider's web
/// widget on a portal page sends the page the person has open with the message. SPM-381: every refusal
/// answers the status the portal's endpoints give its code, through the platform's
/// <c>NhAssistantHttpStatuses</c>.
/// </summary>
[ApiController]
[Route("assistant-channel")]
public sealed class AssistantChannelController(SampleAssistantMessagingChannel channel) : ControllerBase
{
    [HttpPost("links")]
    [Authorize(Policy = SampleAssistantComposition.AccessPolicy)]
    [EndpointSummary("Link a chat to the messaging app")]
    [EndpointDescription("Issues the link token with which the messaging provider delivers the signed-in person's messages to this chat, in the person's active division. Whether the person may use the chat is checked on every delivery, so a link to someone else's chat finds nothing. The link is bound to the person's security stamp: a password change or a revocation of the person's sessions ends it. 400 without an active division, 401 without a sign-in, 403 without the assistant's access policy or from an impersonation session (assistant-not-allowed-while-impersonating), because the link would act as the person after the impersonation ended.")]
    [ProducesResponseType<AssistantChannelLinkViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Link([FromBody] AssistantChannelLinkMutateModel model, CancellationToken cancellationToken)
    {
        if (HttpContext.GetUserId() is not { } userId)
        {
            return Unauthorized();
        }
        if (HttpContext.GetActiveDivisionId() is not { } divisionId)
        {
            return BadRequest(new AssistantChannelErrorViewModel(SampleAssistantMessagingChannel.DeliveryInvalidCode));
        }

        var linked = await channel.LinkAsync(HttpContext.User, model.ConversationId, userId, divisionId, cancellationToken);
        if (!linked.Success)
        {
            return Refusal(linked);
        }

        var link = linked.Data!;
        return Ok(new AssistantChannelLinkViewModel(link.Token, link.ExpiresAt));
    }

    [HttpPost("messages")]
    [AllowAnonymous]
    [EndpointSummary("Send a person's message from the messaging app")]
    [EndpointDescription("The messaging provider delivers a person's message with the link token of their chat. The turn runs as the person the token names, with the checks of POST /api/assistant/conversations/{id}/messages and the person's own rights for every tool, and answers with the assistant's text and, when the turn waits for the person, the approval card to answer through POST assistant-channel/decisions. The turn keeps running when the delivery gives up; the chat in the portal holds its result. SPM-358: files carries the attachments POST assistant-channel/files uploaded to the chat. SPM-380: page carries the portal page the person has open when the provider's web widget on it delivers the message; the turn shows it to the assistant as the person's screen. 400 for an unknown, altered or expired token, a link that a password change or session revocation ended, an invalid message, or a page the platform would not keep whole (assistant-message-invalid), 403 when the person may not use the assistant or no longer sign in, 404 for a chat the person does not take part in, 409 when the chat is busy or the delivery repeats one. SPM-381: every refusal answers the status the portal's endpoints give its code.")]
    [ProducesResponseType<AssistantChannelAnswerViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Send([FromBody] AssistantChannelMessageMutateModel model, CancellationToken cancellationToken)
    {
        var sent = await channel.SendAsync(
            new SampleAssistantChannelMessage(model.DeliveryId, model.Token, model.Text)
            {
                Files = model.Files.Select(file => new NhAiFileReference(file.Id, file.Name, file.MediaType, file.Size, file.Hash)).ToArray(),
                Page = model.Page is null
                    ? null
                    : new NhAssistantClientContextDto(
                        model.Page.Route,
                        model.Page.Title,
                        model.Page.Entities.Select(entity => new NhAssistantClientEntityDto(entity.Type, entity.Id, entity.Label)).ToArray())
            },
            cancellationToken);
        return sent.Success ? Ok(ToViewModel(sent.Data!)) : Refusal(sent);
    }

    [HttpPost("decisions")]
    [AllowAnonymous]
    [EndpointSummary("Answer an approval card from the messaging app")]
    [EndpointDescription("The messaging provider delivers the person's answer to an approval card with the chat's link token: yes or approve approves, no or reject rejects, bound to the card's proposal hash, with the checks of POST /api/assistant/conversations/{id}/approvals/{approvalId}/decide. Answers with the resumed turn's result. 400 for an unknown, altered or expired token, a link that a password change or session revocation ended, or another text, 403 when the person may not decide it or no longer sign in, 404 for an unknown approval or chat, 409 when the proposal changed, was decided or expired.")]
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
    [EndpointDescription("SPM-341: the messaging provider delivers the person's request for a new chat with an agent, with a link token of one of the person's chats. The platform's INhAssistantConversations starts the chat as the person the token names, with the checks of POST /api/assistant/conversations, and the answer carries the link token of the new chat, which the messaging app uses from then on and which expires with the presented link. 400 for an unknown, altered or expired token, a link that a password change or session revocation ended, or a title longer than 200 characters, 403 when the person may not use the assistant or the agent or no longer sign in, 404 for an unknown agent or one that is not offered in the chat.")]
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

    [HttpPost("chat-list")]
    [AllowAnonymous]
    [EndpointSummary("List the person's chats in the messaging app")]
    [EndpointDescription("SPM-358: the messaging app lists the chats of the person a link token names, most recently updated first and ten to a page, through the platform's INhAssistantConversations.ListAsync with the checks of GET /api/assistant/conversations. Each chat carries a link token of its own, which expires with the presented link, so the messaging app continues in the chat the person picks; agents' threads are not listed. The token travels in the body, never in the address. 400 for an unknown, altered or expired token, a link that a password change or session revocation ended, or a page below 1, 403 when the person may not use the assistant or no longer sign in.")]
    [ProducesResponseType<AssistantChannelChatListViewModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListChats([FromBody] AssistantChannelChatListMutateModel model, CancellationToken cancellationToken)
    {
        var listed = await channel.ListChatsAsync(model.Token, model.Page, cancellationToken);
        if (!listed.Success)
        {
            return Refusal(listed);
        }

        var chats = listed.Data!;
        return Ok(new AssistantChannelChatListViewModel(
            chats.Items.Select(chat => new AssistantChannelChatItemViewModel(chat.Token, chat.Title, chat.AgentId, chat.Status, chat.Unread)).ToArray(),
            chats.Total));
    }

    [HttpPost("agents")]
    [AllowAnonymous]
    [EndpointSummary("List the agents the person may chat with in the messaging app")]
    [EndpointDescription("SPM-358: the agents the person a link token names may start a chat with through POST assistant-channel/chats, and what they may attach to a message to each, through the platform's INhAssistantConversations.ListAgentsAsync with the checks of GET /api/assistant/agents. SPM-359: an agent whose model profile, and its fallbacks, may not receive the uploads' classification takes no attachments. 400 for an unknown, altered or expired token, or a link that a password change or session revocation ended, 403 when the person may not use the assistant or no longer sign in.")]
    [ProducesResponseType<AssistantChannelAgentViewModel[]>(StatusCodes.Status200OK)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAgents([FromBody] AssistantChannelTokenMutateModel model, CancellationToken cancellationToken)
    {
        var listed = await channel.ListAgentsAsync(model.Token, cancellationToken);
        if (!listed.Success)
        {
            return Refusal(listed);
        }

        return Ok(listed.Data!
            .Select(agent => new AssistantChannelAgentViewModel(agent.AgentId, agent.DisplayNameKey, agent.AcceptsFiles, agent.AcceptedMediaTypes, agent.MaxFileBytes))
            .ToArray());
    }

    [HttpPost("files")]
    [AllowAnonymous]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
    [EndpointSummary("Upload an attachment from the messaging app")]
    [EndpointDescription("SPM-358: the messaging provider delivers an attachment for the linked chat as multipart/form-data with the chat's link token in the token field and one file part named file. The platform's INhAssistantConversations.UploadAsync uploads it as the person the token names, with the checks of POST /api/assistant/conversations/{id}/files: the chat, the agent's policy and rules, the type against the content and the size; the sample's scanner then checks it and the media library stores it in the chat's folder. The answer is the reference that files of POST assistant-channel/messages carries. The file's name never reaches a log. 400 for an unknown, altered or expired token, a link that a password change or session revocation ended, or a file without a name or content, 403 when the person may not use the assistant or the agent or no longer sign in, 404 for a chat the person does not take part in or a host that takes no uploads, 409 when the agent takes no attachments (SPM-359: also when its profile chain may not receive them), 413 for a file larger than the agent takes, 415 for a type that is not accepted or content of another type, 422 when the scanner refuses the file, 503 when it could not be stored.")]
    [ProducesResponseType<AssistantChannelFileViewModel>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<AssistantChannelErrorViewModel>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Upload([FromForm] AssistantChannelFileMutateModel model, CancellationToken cancellationToken)
    {
        if (model.File is not { } file)
        {
            return BadRequest(new AssistantChannelErrorViewModel(NhAssistantErrorCodes.FileUploadInvalid));
        }

        // The form buffered the file, so the platform can read it again for its checks.
        await using var content = file.OpenReadStream();
        var uploaded = await channel.UploadAsync(
            new SampleAssistantChannelFile(model.Token, file.FileName, file.ContentType, content),
            cancellationToken);
        if (!uploaded.Success)
        {
            return Refusal(uploaded);
        }

        var reference = uploaded.Data!;
        return Ok(new AssistantChannelFileViewModel(reference.Id, reference.Name, reference.MediaType, reference.Size, reference.Hash));
    }

    [HttpPost("chat")]
    [AllowAnonymous]
    [EndpointSummary("Read the linked chat from the messaging app")]
    [EndpointDescription("SPM-341: the messaging app shows the linked chat after a pause: its status, its title, the assistant's last answer and the approval or question card it waits for, read as the person the link token names with the checks of GET /api/assistant/conversations/{id}. The token travels in the body, never in the address. 400 for an unknown, altered or expired token, or a link that a password change or session revocation ended, 403 when the person may not use the assistant or no longer sign in, 404 for a chat the person does not take part in.")]
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
    [EndpointDescription("SPM-341: the messaging provider delivers the person's reply to a question card with the chat's link token: the number of a choice as the card shows them, from 1, or the choice's text. The platform's INhAssistantConversationTurns.AnswerAsync answers as the person the token names, with the checks of POST /api/assistant/conversations/{id}/questions/{questionId}/answer, and the turn continues with that person's rights. Answers with the resumed turn's result. 400 for an unknown, altered or expired token, a link that a password change or session revocation ended, or a reply that names no choice, 403 when the question asks another person or the person may not use the assistant or no longer sign in, 404 for an unknown question or chat, 409 when the question was answered, stopped or expired.")]
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
    [EndpointDescription("SPM-341: the person stops the linked chat's turn from the messaging app, as the portal's stop button does with POST /api/assistant/conversations/{id}/cancel: a running turn is cancelled, a waiting approval rejected and a waiting question cancelled; a chat without a turn stays as it is. 400 for an unknown, altered or expired token, or a link that a password change or session revocation ended, 403 when the person neither owns the chat nor started its turn, may not use the assistant or no longer sign in, 404 for a chat the person does not take part in.")]
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

    /// <summary>
    /// SPM-358: the largest upload request: the platform's largest file (20 MB) and the form around it.
    /// </summary>
    private const long MaxUploadRequestBytes = 20L * 1024 * 1024 + 64 * 1024;

    private IActionResult Refusal(TaskResult result)
    {
        var code = result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? SampleAssistantMessagingChannel.DeliveryInvalidCode;
        // SPM-381: the status the portal's endpoints give the code, attachments (SPM-358) included.
        return StatusCode(SampleAssistantMessagingChannel.StatusOf(code), new AssistantChannelErrorViewModel(code));
    }
}
