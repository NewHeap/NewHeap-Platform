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
/// </summary>
public sealed class SampleAssistantMessagingChannel(
    INhAssistantConversationTurns turns,
    INhUserManager users,
    INhAuthenticationService authentication,
    IDataProtectionProvider dataProtection)
{
    /// <summary>
    /// The channel the platform's audit records on the events of these turns.
    /// </summary>
    public const string Channel = "messaging-chat";

    /// <summary>
    /// The refusal of a delivery whose link token this channel did not issue, that expired, or of an
    /// answer to an approval card that is neither an approval nor a rejection.
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
        if (ReadToken(message.Token) is not { } link)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(DeliveryInvalidCode, "The delivery does not belong to a linked chat.");
        }
        if (await PersonAsync(link.UserId, cancellationToken) is not { } person)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(PersonUnavailableCode, "The person may not sign in.");
        }

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
        if (ReadToken(decision.Token) is not { } link
            || DecisionOf(decision.Text) is not { } answer
            || string.IsNullOrWhiteSpace(decision.ProposalHash))
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(DeliveryInvalidCode, "The answer does not decide an approval of a linked chat.");
        }
        if (await PersonAsync(link.UserId, cancellationToken) is not { } person)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(PersonUnavailableCode, "The person may not sign in.");
        }

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

    /// <summary>
    /// The text the messaging app shows, and the approval card when the turn waits for the person.
    /// </summary>
    private static SampleAssistantChannelAnswer AnswerOf(NhAssistantConversationTurnResult result)
    {
        var text = result.Message is null
            ? null
            : string.Concat(result.Message.Parts.OfType<NhAssistantTextPartDto>().Select(part => part.Text));
        var approval = result.PendingApprovals.FirstOrDefault();
        return new SampleAssistantChannelAnswer(
            result.Status,
            result.ErrorCode,
            string.IsNullOrWhiteSpace(text) ? null : text,
            approval is null ? null : new SampleAssistantChannelApproval(approval.ApprovalId, approval.ProposalHash, approval.Summary));
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
/// What the messaging app shows after a turn: its status and code, the assistant's text and, when the
/// turn waits for the person, the approval card.
/// </summary>
public sealed record SampleAssistantChannelAnswer(
    string Status,
    string? Code,
    string? Text,
    SampleAssistantChannelApproval? Approval);

/// <summary>
/// The approval card of a turn that waits for the person: what to answer and the summary to show.
/// </summary>
public sealed record SampleAssistantChannelApproval(Guid ApprovalId, string ProposalHash, string Summary);
