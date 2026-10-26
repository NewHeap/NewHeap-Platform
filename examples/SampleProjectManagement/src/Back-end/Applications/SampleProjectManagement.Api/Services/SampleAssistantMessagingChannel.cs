using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-332: a messaging channel in which a person talks with the project assistant outside the
/// portal. The person links one of their chats to the messaging app from the portal, signed in: the
/// application issues a link token that names the chat, the person and their active division. The
/// messaging provider then delivers the person's messages and their answers to approval cards with that
/// token to <c>POST assistant-channel/messages</c> and <c>POST assistant-channel/decisions</c>. The
/// channel vouches for the person because only the token it issued to them says who writes; it rebuilds
/// their principal from the user store as their own sign-in would, and the platform's
/// <see cref="INhAssistantConversationTurns"/> runs the turn as that person, with the endpoints' access
/// rules and the person's own rights for every tool, without a request of the person. The turn keeps
/// running when the provider's webhook gives up, and the portal shows the same chat.
/// <para>
/// SPM-341: the person also starts a new chat with an agent, reads the linked chat, answers the
/// question its turn asks and stops its turn from the messaging app, through the platform's
/// <see cref="INhAssistantConversations"/> and <see cref="INhAssistantConversationTurns.AnswerAsync"/>,
/// with the checks and codes of the endpoints the portal calls.
/// </para>
/// </summary>
public sealed class SampleAssistantMessagingChannel(
    INhAssistantConversationTurns turns,
    INhAssistantConversations conversations,
    INhUserManager users,
    INhAuthenticationService authentication,
    IDataProtectionProvider dataProtection)
{
    /// <summary>
    /// The channel the platform's audit records on the events of these turns.
    /// </summary>
    public const string Channel = "messaging-chat";

    /// <summary>
    /// The refusal of a delivery whose link token this channel did not issue, that expired, of an answer
    /// to an approval card that is neither an approval nor a rejection, or of an answer that names none
    /// of the question's choices.
    /// </summary>
    public const string DeliveryInvalidCode = "sample-assistant-channel-delivery-invalid";

    /// <summary>
    /// The refusal of a person who may no longer sign in, or who does not exist.
    /// </summary>
    public const string PersonUnavailableCode = "sample-assistant-channel-person-unavailable";

    /// <summary>
    /// How long a link stays valid before the person links the chat again.
    /// </summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);

    private const string Purpose = "SampleProjectManagement.AssistantChannel.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITimeLimitedDataProtector _tokens = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>
    /// The link token of <paramref name="userId"/>'s chat <paramref name="conversationId"/> in their active
    /// division, for the portal's signed-in person only. Whether the person may use the chat is checked on
    /// every delivery, so a link of someone else's chat finds nothing.
    /// </summary>
    public SampleAssistantChannelLink Link(Guid conversationId, Guid userId, Guid divisionId)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(LinkLifetime);
        var token = _tokens.Protect(
            JsonSerializer.Serialize(new LinkToken(conversationId, userId, divisionId), Json),
            expiresAt);
        return new SampleAssistantChannelLink(token, expiresAt);
    }

    /// <summary>
    /// Sends the delivered message as the person the link names and answers with the turn's result. The
    /// delivery id is the message's client message id, so a delivery the provider repeats starts no second
    /// turn (<c>assistant-message-duplicate</c>).
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelAnswer>> SendAsync(
        SampleAssistantChannelMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var linked = await LinkedPersonAsync(message.Token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(linked);
        }

        var (link, person) = linked.Data;
        var sent = await turns.SendAsync(
            person,
            link.ConversationId,
            new NhAssistantConversationMessage(message.Text) { ClientMessageId = message.DeliveryId },
            OptionsOf(link),
            cancellationToken);
        return sent.Success
            ? TaskResult<SampleAssistantChannelAnswer>.Succeeded(AnswerOf(sent.Data!))
            : TaskResult<SampleAssistantChannelAnswer>.Failed(sent);
    }

    /// <summary>
    /// Decides the approval card the person answered: "yes" or "approve" approves, "no" or "reject"
    /// rejects, bound to the proposal hash the card showed. Answers with the resumed turn's result.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelAnswer>> DecideAsync(
        SampleAssistantChannelDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (DecisionOf(decision.Text) is not { } answer || string.IsNullOrWhiteSpace(decision.ProposalHash))
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(DeliveryInvalidCode, "The answer does not decide an approval of a linked chat.");
        }

        var linked = await LinkedPersonAsync(decision.Token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(linked);
        }

        var (link, person) = linked.Data;
        var decided = await turns.DecideAsync(
            person,
            link.ConversationId,
            decision.ApprovalId,
            new NhAssistantConversationDecision(answer, decision.ProposalHash),
            OptionsOf(link),
            cancellationToken);
        return decided.Success
            ? TaskResult<SampleAssistantChannelAnswer>.Succeeded(AnswerOf(decided.Data!))
            : TaskResult<SampleAssistantChannelAnswer>.Failed(decided);
    }

    /// <summary>
    /// SPM-341: starts a new chat of the person the link names with <paramref name="start"/>'s agent, in
    /// the link's division, and answers with the link token of the new chat, so the messaging app
    /// continues there. The platform refuses an agent the person may not use
    /// (<c>assistant-agent-forbidden</c>) or that is not offered in the chat
    /// (<c>assistant-agent-not-found</c>), as <c>POST /api/assistant/conversations</c> does.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelChat>> StartChatAsync(
        SampleAssistantChannelStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (string.IsNullOrWhiteSpace(start.AgentId))
        {
            return TaskResult<SampleAssistantChannelChat>.Failed(DeliveryInvalidCode, "The new chat names no agent.");
        }

        var linked = await LinkedPersonAsync(start.Token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelChat>.Failed(linked);
        }

        var (link, person) = linked.Data;
        var created = await conversations.CreateAsync(
            person,
            new NhAssistantNewConversation(start.AgentId.Trim()) { Title = start.Title },
            ConversationOptionsOf(link),
            cancellationToken);
        if (!created.Success)
        {
            return TaskResult<SampleAssistantChannelChat>.Failed(created);
        }

        var chat = created.Data!;
        var newLink = Link(chat.Id, link.UserId, link.DivisionId);
        return TaskResult<SampleAssistantChannelChat>.Succeeded(
            new SampleAssistantChannelChat(newLink.Token, newLink.ExpiresAt, StateOf(chat)));
    }

    /// <summary>
    /// SPM-341: the linked chat as the messaging app shows it after a pause: its status, the assistant's
    /// last answer and the approval or question card it waits for, as
    /// <c>GET /api/assistant/conversations/{id}</c> returns it to the person.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelState>> ReadAsync(string token, CancellationToken cancellationToken)
    {
        var linked = await LinkedPersonAsync(token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelState>.Failed(linked);
        }

        var (link, person) = linked.Data;
        var chat = await conversations.GetAsync(person, link.ConversationId, ConversationOptionsOf(link), cancellationToken);
        return chat.Success
            ? TaskResult<SampleAssistantChannelState>.Succeeded(StateOf(chat.Data!))
            : TaskResult<SampleAssistantChannelState>.Failed(chat);
    }

    /// <summary>
    /// SPM-341: answers the question card the person replied to, with the number of a choice as the card
    /// shows them (1 for the first) or the choice's own text, and answers with the resumed turn's result.
    /// The platform checks the question in the order of the answer endpoint: unknown, asked of another
    /// person, no longer waiting, a choice outside the question, expired.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelAnswer>> AnswerAsync(
        SampleAssistantChannelQuestionAnswer answer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var linked = await LinkedPersonAsync(answer.Token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(linked);
        }

        var (link, person) = linked.Data;
        // The card numbers the choices; the chat names them, so a reply of the choice's text works too.
        var chat = await conversations.GetAsync(person, link.ConversationId, ConversationOptionsOf(link), cancellationToken);
        if (!chat.Success)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(chat);
        }
        IReadOnlyList<string> choices = chat.Data!.PendingQuestion is NhAssistantQuestionPartDto waiting && waiting.QuestionId == answer.QuestionId
            ? waiting.Choices
            : [];
        if (ChoiceOf(answer.Text, choices) is not { } choice)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(DeliveryInvalidCode, "The answer names none of the question's choices.");
        }

        var answered = await turns.AnswerAsync(
            person,
            link.ConversationId,
            answer.QuestionId,
            new NhAssistantConversationAnswer(choice),
            OptionsOf(link),
            cancellationToken);
        return answered.Success
            ? TaskResult<SampleAssistantChannelAnswer>.Succeeded(AnswerOf(answered.Data!))
            : TaskResult<SampleAssistantChannelAnswer>.Failed(answered);
    }

    /// <summary>
    /// SPM-341: stops the turn of the linked chat, as the portal's stop button does: a running turn is
    /// cancelled, a waiting approval rejected and a waiting question cancelled. Only the chat's owner or
    /// the person who started the turn stops it (<c>assistant-turn-forbidden</c>).
    /// </summary>
    public async Task<TaskResult> StopAsync(string token, CancellationToken cancellationToken)
    {
        var linked = await LinkedPersonAsync(token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult.Failed(linked);
        }

        var (link, person) = linked.Data;
        return await conversations.CancelAsync(person, link.ConversationId, ConversationOptionsOf(link), cancellationToken);
    }

    /// <summary>
    /// The turn works in the person's active division, as the portal's requests do, and keeps running
    /// when the provider's webhook gives up before it ended: the chat then holds the answer.
    /// </summary>
    private static NhAssistantConversationTurnOptions OptionsOf(LinkToken link)
    {
        return new NhAssistantConversationTurnOptions(Channel)
        {
            ActiveDivisionId = link.DivisionId,
            KeepTurnAfterCallerLeaves = true
        };
    }

    private static NhAssistantConversationOptions ConversationOptionsOf(LinkToken link)
    {
        return new NhAssistantConversationOptions { ActiveDivisionId = link.DivisionId };
    }

    /// <summary>
    /// The text the messaging app shows, and the approval or question card when the turn waits for the
    /// person.
    /// </summary>
    private static SampleAssistantChannelAnswer AnswerOf(NhAssistantConversationTurnResult result)
    {
        var approval = result.PendingApprovals.FirstOrDefault();
        return new SampleAssistantChannelAnswer(
            result.Status,
            result.ErrorCode,
            TextOf(result.Message),
            approval is null ? null : new SampleAssistantChannelApproval(approval.ApprovalId, approval.ProposalHash, approval.Summary))
        {
            Question = QuestionOf(result.PendingQuestion)
        };
    }

    /// <summary>
    /// What the messaging app shows of a chat: its status, the assistant's last answer and its cards.
    /// </summary>
    private static SampleAssistantChannelState StateOf(NhAssistantConversationDto chat)
    {
        var lastAnswer = chat.Messages.LastOrDefault(message => message.Role == "assistant");
        var approval = chat.PendingApproval as NhAssistantApprovalPartDto;
        return new SampleAssistantChannelState(
            chat.Status,
            chat.Title,
            TextOf(lastAnswer),
            approval is null ? null : new SampleAssistantChannelApproval(approval.ApprovalId, approval.ProposalHash, approval.Summary),
            QuestionOf(chat.PendingQuestion as NhAssistantQuestionPartDto));
    }

    private static SampleAssistantChannelQuestion? QuestionOf(NhAssistantQuestionPartDto? question)
    {
        return question is null ? null : new SampleAssistantChannelQuestion(question.QuestionId, question.Question, question.Choices);
    }

    private static string? TextOf(NhAssistantMessageDto? message)
    {
        var text = message is null
            ? null
            : string.Concat(message.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// The link and the person it names, as their own sign-in would authenticate them; a forged, altered
    /// or expired token and a person who may no longer sign in are refused.
    /// </summary>
    private async Task<TaskResult<(LinkToken Link, NhAssistantDecidingPerson Person)>> LinkedPersonAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        if (ReadToken(token) is not { } link)
        {
            return TaskResult<(LinkToken, NhAssistantDecidingPerson)>.Failed(DeliveryInvalidCode, "The delivery does not belong to a linked chat.");
        }
        if (await PersonAsync(link.UserId, cancellationToken) is not { } person)
        {
            return TaskResult<(LinkToken, NhAssistantDecidingPerson)>.Failed(PersonUnavailableCode, "The person may not sign in.");
        }

        return TaskResult<(LinkToken, NhAssistantDecidingPerson)>.Succeeded((link, person));
    }

    /// <summary>
    /// The person as their own sign-in would authenticate them: an account that may sign in, with the
    /// claims of the user store.
    /// </summary>
    private async Task<NhAssistantDecidingPerson?> PersonAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!await authentication.MaySignInAsync(userId, cancellationToken))
        {
            return null;
        }

        var claims = await users.GetValidClaimsByUserIdAsync(userId, true, cancellationToken);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Channel));
        return new NhAssistantDecidingPerson(principal, userId.ToString("D"));
    }

    private LinkToken? ReadToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LinkToken>(_tokens.Unprotect(token), Json);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException or JsonException)
        {
            // Forged, altered or expired.
            return null;
        }
    }

    private static NhAssistantProposalDecision? DecisionOf(string? text)
    {
        return text?.Trim().ToLowerInvariant() switch
        {
            "yes" or "approve" => NhAssistantProposalDecision.Approve,
            "no" or "reject" => NhAssistantProposalDecision.Reject,
            _ => null
        };
    }

    /// <summary>
    /// The zero-based choice a reply names: the number the card shows (1 for the first) or the choice's
    /// text, ignoring case. A number outside the card is passed on, so the platform refuses it with
    /// <c>assistant-question-answer-invalid</c>; a text that names no choice is no answer.
    /// </summary>
    private static int? ChoiceOf(string? text, IReadOnlyList<string> choices)
    {
        var reply = text?.Trim();
        if (string.IsNullOrEmpty(reply))
        {
            return null;
        }
        if (int.TryParse(reply, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 1)
        {
            return number - 1;
        }

        for (var index = 0; index < choices.Count; index++)
        {
            if (string.Equals(choices[index], reply, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return null;
    }

    private sealed record LinkToken(Guid ConversationId, Guid UserId, Guid DivisionId);
}

/// <summary>
/// The link token of a chat the person continues in the messaging app, and until when it is valid.
/// </summary>
public sealed record SampleAssistantChannelLink(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// A message the messaging provider delivered: its own id, the chat's link token and the text.
/// </summary>
public sealed record SampleAssistantChannelMessage(string DeliveryId, string Token, string Text);

/// <summary>
/// An answer to an approval card: the chat's link token, the approval and the proposal hash the card
/// showed, and the text of the answer.
/// </summary>
public sealed record SampleAssistantChannelDecision(string Token, Guid ApprovalId, string ProposalHash, string Text);

/// <summary>
/// SPM-341: a new chat the person starts from the messaging app: a link token of theirs, the agent and
/// an optional title.
/// </summary>
public sealed record SampleAssistantChannelStart(string Token, string AgentId, string? Title);

/// <summary>
/// SPM-341: an answer to a question card: the chat's link token, the question and the reply, the number
/// of a choice or its text.
/// </summary>
public sealed record SampleAssistantChannelQuestionAnswer(string Token, Guid QuestionId, string Text);

/// <summary>
/// What the messaging app shows after a turn: its status and code, the assistant's text and, when the
/// turn waits for the person, the approval card or, since SPM-341, the question card.
/// </summary>
public sealed record SampleAssistantChannelAnswer(
    string Status,
    string? Code,
    string? Text,
    SampleAssistantChannelApproval? Approval)
{
    public SampleAssistantChannelQuestion? Question { get; init; }
}

/// <summary>
/// The approval card of a turn that waits for the person: what to answer and the summary to show.
/// </summary>
public sealed record SampleAssistantChannelApproval(Guid ApprovalId, string ProposalHash, string Summary);

/// <summary>
/// SPM-341: the question card of a turn that waits for the person's answer: the question and its
/// choices, which the card numbers from 1.
/// </summary>
public sealed record SampleAssistantChannelQuestion(Guid QuestionId, string Question, IReadOnlyList<string> Choices);

/// <summary>
/// SPM-341: a new chat: its link token, until when it is valid, and the chat as the app shows it.
/// </summary>
public sealed record SampleAssistantChannelChat(string Token, DateTimeOffset ExpiresAt, SampleAssistantChannelState Chat);

/// <summary>
/// SPM-341: a chat as the messaging app shows it: its status and title, the assistant's last answer and
/// the card it waits for.
/// </summary>
public sealed record SampleAssistantChannelState(
    string Status,
    string? Title,
    string? LastAnswer,
    SampleAssistantChannelApproval? Approval,
    SampleAssistantChannelQuestion? Question);
