using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
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
/// <para>
/// SPM-358: the messaging app lists the person's chats, each with a link token of its own, and the
/// agents the person may chat with and what they may attach, and uploads an attachment to the linked
/// chat, which the person's next message carries, through <see cref="INhAssistantConversations"/>'s
/// <c>ListAsync</c>, <c>ListAgentsAsync</c> and <c>UploadAsync</c>, with the checks and codes of the
/// portal's endpoints. The upload reads the delivered file again for its content check, the
/// application's scanner and the media library, and its name never reaches a log.
/// </para>
/// <para>
/// A link is a credential of the person, so only the person's own session issues one: an impersonation
/// session gets none (<see cref="NhAssistantErrorCodes.NotAllowedWhileImpersonating"/>), because the link
/// would act as the person after the impersonation ended. Every link is bound to the person's security
/// stamp, so changing the password or revoking the person's sessions ends it, and a new chat's link
/// expires with the link that started it.
/// </para>
/// <para>
/// SPM-380: the provider's web widget on a portal page delivers the page the person has open with the
/// message, which <see cref="NhAssistantConversationMessage.ClientContext"/> carries to the turn as the
/// person's screen, validated and stored as the portal's own messages are. SPM-381: every refusal answers
/// the HTTP status the portal's endpoints give its code (<see cref="StatusOf"/>), through the platform's
/// <see cref="NhAssistantHttpStatuses"/> instead of a table of the channel's own.
/// </para>
/// <para>
/// SPM-383: a turn reports the tools it uses (<see cref="NhAssistantConversationTurnOptions.Progress"/>)
/// and how long it took (<see cref="NhAssistantConversationTurnResult.Timings"/>), and when the provider's
/// webhook gave up, <see cref="WaitAsync"/> waits for the same turn instead of polling the chat.
/// </para>
/// </summary>
public sealed class SampleAssistantMessagingChannel(
    INhAssistantConversationTurns turns,
    INhAssistantConversations conversations,
    INhUserManager<NhUser> users,
    INhAuthenticationService authentication,
    IDataProtectionProvider dataProtection)
{
    /// <summary>
    /// The channel the platform's audit records on the events of these turns.
    /// </summary>
    public const string Channel = "messaging-chat";

    /// <summary>
    /// The refusal of a delivery whose link token this channel did not issue, that expired or that the
    /// person's password change or session revocation ended, of an answer to an approval card that is
    /// neither an approval nor a rejection, or of an answer that names none of the question's choices.
    /// </summary>
    public const string DeliveryInvalidCode = "sample-assistant-channel-delivery-invalid";

    /// <summary>
    /// The refusal of a person who may no longer sign in, or who does not exist.
    /// </summary>
    public const string PersonUnavailableCode = "sample-assistant-channel-person-unavailable";

    /// <summary>
    /// SPM-381: the HTTP status of a refusal with <paramref name="code"/>: the channel's own codes first,
    /// then the status the portal's endpoints give a platform code, so the messaging app and the portal
    /// answer a refusal alike. A code the platform does not know answers <c>400</c>.
    /// </summary>
    public static int StatusOf(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code switch
        {
            PersonUnavailableCode => StatusCodes.Status403Forbidden,
            DeliveryInvalidCode => StatusCodes.Status400BadRequest,
            _ => NhAssistantHttpStatuses.Of(code)
        };
    }

    /// <summary>
    /// SPM-358: the chats of one page of <see cref="ListChatsAsync"/>.
    /// </summary>
    public const int ChatsPerPage = 10;

    /// <summary>
    /// How long a link stays valid before the person links the chat again. A chat started through a link
    /// gets a link that expires with it.
    /// </summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(30);

    // v2 binds every link to the person's security stamp; a link of v1 no longer reads.
    private const string Purpose = "SampleProjectManagement.AssistantChannel.v2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// SPM-383: the longest a delivery of <see cref="WaitAsync"/> waits; a webhook does not stay open longer.
    /// </summary>
    public const int MaxWaitSeconds = 60;

    private readonly ITimeLimitedDataProtector _tokens = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>
    /// The link token of <paramref name="userId"/>'s chat <paramref name="conversationId"/> in their active
    /// division, for the portal's signed-in person in their own session only: <paramref name="user"/> of an
    /// impersonation session gets none (<see cref="NhAssistantErrorCodes.NotAllowedWhileImpersonating"/>).
    /// The link is bound to the person's current security stamp. Whether the person may use the chat is
    /// checked on every delivery, so a link of someone else's chat finds nothing.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelLink>> LinkAsync(
        ClaimsPrincipal user,
        Guid conversationId,
        Guid userId,
        Guid divisionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.HasClaim(claim => claim.Type == NhPlatformClaimTypes.ImpersonateOriginUserId))
        {
            return TaskResult<SampleAssistantChannelLink>.Failed(
                NhAssistantErrorCodes.NotAllowedWhileImpersonating,
                "An impersonation session links no chat of the person it acts as.");
        }
        if (await StampOfAsync(userId) is not { } stamp)
        {
            return TaskResult<SampleAssistantChannelLink>.Failed(PersonUnavailableCode, "The person does not exist.");
        }

        return TaskResult<SampleAssistantChannelLink>.Succeeded(
            Protect(new LinkToken(conversationId, userId, divisionId, stamp), DateTimeOffset.UtcNow.Add(LinkLifetime)));
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

        var (link, _, person) = linked.Data;
        var activity = new SampleAssistantChannelActivity();
        var sent = await turns.SendAsync(
            person,
            link.ConversationId,
            new NhAssistantConversationMessage(message.Text)
            {
                ClientMessageId = message.DeliveryId,
                // SPM-358: the attachments the messaging app uploaded to the chat before.
                Files = message.Files,
                // SPM-380: the page the person has open around the provider's web widget, if any.
                ClientContext = message.Page
            },
            OptionsOf(link) with { Progress = activity },
            cancellationToken);
        return sent.Success
            ? TaskResult<SampleAssistantChannelAnswer>.Succeeded(AnswerOf(sent.Data!) with { Tools = activity.Tools })
            : TaskResult<SampleAssistantChannelAnswer>.Failed(sent);
    }

    /// <summary>
    /// SPM-383: waits for the linked chat's turn, which the provider's webhook may have given up on, at
    /// most <paramref name="seconds"/>: the answer of a turn that ended, or status <c>running</c>. Nothing
    /// polls: the platform's <see cref="INhAssistantConversationTurns.WaitAsync"/> follows the turn itself,
    /// with the checks of a turn, and leaves it alone when the delivery leaves.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelAnswer>> WaitAsync(
        string token,
        int seconds,
        CancellationToken cancellationToken)
    {
        if (seconds is < 0 or > MaxWaitSeconds)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(DeliveryInvalidCode, "A wait lasts from zero to a minute.");
        }

        var linked = await LinkedPersonAsync(token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelAnswer>.Failed(linked);
        }

        var (link, _, person) = linked.Data;
        var activity = new SampleAssistantChannelActivity();
        var waited = await turns.WaitAsync(
            person,
            link.ConversationId,
            new NhAssistantTurnWait(TimeSpan.FromSeconds(seconds))
            {
                ActiveDivisionId = link.DivisionId,
                Progress = activity
            },
            cancellationToken);
        return waited.Success
            ? TaskResult<SampleAssistantChannelAnswer>.Succeeded(AnswerOf(waited.Data!) with { Tools = activity.Tools })
            : TaskResult<SampleAssistantChannelAnswer>.Failed(waited);
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

        var (link, _, person) = linked.Data;
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
    /// the link's division, and answers with the link token of the new chat, which expires with the
    /// presented link, so the messaging app continues there. The platform refuses an agent the person
    /// may not use (<c>assistant-agent-forbidden</c>) or that is not offered in the chat
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

        var (link, _, person) = linked.Data;
        var created = await conversations.CreateAsync(
            person,
            new NhAssistantNewConversation(start.AgentId.Trim()) { Title = start.Title },
            ConversationOptionsOf(link),
            cancellationToken);
        if (!created.Success)
        {
            return TaskResult<SampleAssistantChannelChat>.Failed(created);
        }

        // The new chat's link expires with the link that started it, so starting chats never extends it.
        var chat = created.Data!;
        var newLink = Protect(link with { ConversationId = chat.Id }, linked.Data.ExpiresAt);
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

        var (link, _, person) = linked.Data;
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

        var (link, _, person) = linked.Data;
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

        var (link, _, person) = linked.Data;
        return await conversations.CancelAsync(person, link.ConversationId, ConversationOptionsOf(link), cancellationToken);
    }

    /// <summary>
    /// SPM-358: the person's chats, most recently updated first, as the portal's list shows them, each
    /// with a link token of its own that expires with the presented link, so the messaging app continues
    /// in the chat the person picks. Agents' threads are not listed.
    /// </summary>
    public async Task<TaskResult<SampleAssistantChannelChatList>> ListChatsAsync(
        string token,
        int page,
        CancellationToken cancellationToken)
    {
        var linked = await LinkedPersonAsync(token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<SampleAssistantChannelChatList>.Failed(linked);
        }

        var (link, expiresAt, person) = linked.Data;
        var listed = await conversations.ListAsync(
            person,
            new NhAssistantConversationPage(page, ChatsPerPage),
            ConversationOptionsOf(link),
            cancellationToken);
        if (!listed.Success)
        {
            return TaskResult<SampleAssistantChannelChatList>.Failed(listed);
        }

        var items = listed.Data!.Items
            .Select(chat => new SampleAssistantChannelChatItem(
                Protect(link with { ConversationId = chat.Id }, expiresAt).Token,
                chat.Title,
                chat.AgentId,
                chat.Status,
                chat.LastMessageSequence > chat.LastReadSequence))
            .ToArray();
        return TaskResult<SampleAssistantChannelChatList>.Succeeded(new SampleAssistantChannelChatList(items, listed.Data.Total));
    }

    /// <summary>
    /// SPM-358: the agents the person may start a chat with from the messaging app, as the portal's
    /// panel lists them, and what the person may attach to a message to each. SPM-359: an agent whose
    /// profile chain may not receive the uploads' classification, such as the quick answers agent,
    /// takes no attachments.
    /// </summary>
    public async Task<TaskResult<IReadOnlyList<SampleAssistantChannelAgent>>> ListAgentsAsync(
        string token,
        CancellationToken cancellationToken)
    {
        var linked = await LinkedPersonAsync(token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<IReadOnlyList<SampleAssistantChannelAgent>>.Failed(linked);
        }

        var (link, _, person) = linked.Data;
        var agents = await conversations.ListAgentsAsync(person, ConversationOptionsOf(link), cancellationToken);
        if (!agents.Success)
        {
            return TaskResult<IReadOnlyList<SampleAssistantChannelAgent>>.Failed(agents);
        }

        IReadOnlyList<SampleAssistantChannelAgent> offered = agents.Data!
            .Select(agent => new SampleAssistantChannelAgent(
                agent.Id,
                agent.DisplayNameKey,
                agent.Uploads?.AcceptedMediaTypes ?? [],
                agent.Uploads?.MaxFileBytes ?? 0))
            .ToArray();
        return TaskResult<IReadOnlyList<SampleAssistantChannelAgent>>.Succeeded(offered);
    }

    /// <summary>
    /// SPM-358: uploads an attachment the messaging provider delivered to the linked chat, as the
    /// portal's composer does, and answers with the reference the person's next message carries in
    /// <see cref="SampleAssistantChannelMessage.Files"/>. The platform checks the chat, the agent's
    /// policy and rules, the type against the content and the size, runs the sample's scanner and has
    /// the media library store the file, with the codes of <c>POST /api/assistant/conversations/{id}/files</c>.
    /// </summary>
    public async Task<TaskResult<NhAiFileReference>> UploadAsync(
        SampleAssistantChannelFile file,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var linked = await LinkedPersonAsync(file.Token, cancellationToken);
        if (!linked.Success)
        {
            return TaskResult<NhAiFileReference>.Failed(linked);
        }

        var (link, _, person) = linked.Data;
        return await conversations.UploadAsync(
            person,
            link.ConversationId,
            new NhAssistantConversationUpload(file.FileName, file.MediaType, file.Content),
            ConversationOptionsOf(link),
            cancellationToken);
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
            Question = QuestionOf(result.PendingQuestion),
            Milliseconds = result.Timings is { } timings ? (long)timings.Total.TotalMilliseconds : null
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
    /// The link, until when it is valid, and the person it names, as their own sign-in would authenticate
    /// them. A forged, altered or expired token and a link whose security stamp is no longer the
    /// person's, because the person changed the password or their sessions were revoked, are refused as
    /// invalid; a person who does not exist or may no longer sign in is unavailable.
    /// </summary>
    private async Task<TaskResult<(LinkToken Link, DateTimeOffset ExpiresAt, NhAssistantDecidingPerson Person)>> LinkedPersonAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        if (ReadToken(token) is not { } read)
        {
            return TaskResult<(LinkToken, DateTimeOffset, NhAssistantDecidingPerson)>.Failed(DeliveryInvalidCode, "The delivery does not belong to a linked chat.");
        }

        var (link, expiresAt) = read;
        if (await StampOfAsync(link.UserId) is not { } stamp)
        {
            return TaskResult<(LinkToken, DateTimeOffset, NhAssistantDecidingPerson)>.Failed(PersonUnavailableCode, "The person does not exist.");
        }
        if (!SameStamp(link.Stamp, stamp))
        {
            return TaskResult<(LinkToken, DateTimeOffset, NhAssistantDecidingPerson)>.Failed(DeliveryInvalidCode, "The link ended with the person's password change or session revocation.");
        }
        if (await PersonAsync(link.UserId, cancellationToken) is not { } person)
        {
            return TaskResult<(LinkToken, DateTimeOffset, NhAssistantDecidingPerson)>.Failed(PersonUnavailableCode, "The person may not sign in.");
        }

        return TaskResult<(LinkToken, DateTimeOffset, NhAssistantDecidingPerson)>.Succeeded((link, expiresAt, person));
    }

    /// <summary>
    /// A digest of the person's current security stamp, which a password change and a session revocation
    /// replace, or <see langword="null"/> when the person does not exist. The link carries the digest, never
    /// the stamp itself.
    /// </summary>
    private async Task<string?> StampOfAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(user.SecurityStamp ?? string.Empty)));
    }

    private static bool SameStamp(string? linked, string current)
    {
        return linked is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(linked), Encoding.ASCII.GetBytes(current));
    }

    private SampleAssistantChannelLink Protect(LinkToken link, DateTimeOffset expiresAt)
    {
        var token = _tokens.Protect(JsonSerializer.Serialize(link, Json), expiresAt);
        return new SampleAssistantChannelLink(token, expiresAt);
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
        // A host with tenants passes the tenant the person signs in with; the sample has none.
        return new NhAssistantDecidingPerson(principal, userId.ToString("D"), TenantId: null);
    }

    private (LinkToken Link, DateTimeOffset ExpiresAt)? ReadToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            var payload = _tokens.Unprotect(token, out var expiresAt);
            return JsonSerializer.Deserialize<LinkToken>(payload, Json) is { } link
                ? (link, expiresAt)
                : null;
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

    /// <summary>
    /// What a link names: the chat, the person and their division, and the digest of the person's
    /// security stamp when it was issued.
    /// </summary>
    private sealed record LinkToken(Guid ConversationId, Guid UserId, Guid DivisionId, string Stamp);
}

/// <summary>
/// The link token of a chat the person continues in the messaging app, and until when it is valid.
/// </summary>
public sealed record SampleAssistantChannelLink(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// A message the messaging provider delivered: its own id, the chat's link token and the text.
/// </summary>
public sealed record SampleAssistantChannelMessage(string DeliveryId, string Token, string Text)
{
    /// <summary>
    /// SPM-358: the attachments the messaging app uploaded to the chat with
    /// <see cref="SampleAssistantMessagingChannel.UploadAsync"/>, as their references.
    /// </summary>
    public IReadOnlyList<NhAiFileReference> Files { get; init; } = [];

    /// <summary>
    /// SPM-380: the portal page the person has open when the provider's web widget on it delivers the
    /// message, in the platform's own shape; <see langword="null"/> from the messaging app itself.
    /// </summary>
    public NhAssistantClientContextDto? Page { get; init; }
}

/// <summary>
/// SPM-358: an attachment the messaging provider delivered for the linked chat: the chat's link token,
/// the name and media type the person's device gave it and its content, a stream the upload reads more
/// than once; the caller disposes it.
/// </summary>
public sealed record SampleAssistantChannelFile(string Token, string FileName, string? MediaType, Stream Content);

/// <summary>
/// SPM-358: one page of the person's chats and how many there are.
/// </summary>
public sealed record SampleAssistantChannelChatList(IReadOnlyList<SampleAssistantChannelChatItem> Items, int Total);

/// <summary>
/// SPM-358: a chat of the person in the list: the link token to continue in it, its title, agent and
/// status, and whether it has messages the person did not read.
/// </summary>
public sealed record SampleAssistantChannelChatItem(string Token, string? Title, string AgentId, string Status, bool Unread);

/// <summary>
/// SPM-358: an agent the person may chat with, the key of its name, and the media types and the largest
/// file the person may attach; no types when the agent takes no attachments (SPM-359).
/// </summary>
public sealed record SampleAssistantChannelAgent(
    string AgentId,
    string DisplayNameKey,
    IReadOnlyList<string> AcceptedMediaTypes,
    int MaxFileBytes)
{
    public bool AcceptsFiles => AcceptedMediaTypes.Count > 0;
}

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

    /// <summary>
    /// SPM-383: the names of the tools the turn used while the channel followed it, in order.
    /// </summary>
    public IReadOnlyList<string> Tools { get; init; } = [];

    /// <summary>
    /// SPM-383: how long the turn took, when the channel saw its end.
    /// </summary>
    public long? Milliseconds { get; init; }
}

/// <summary>
/// SPM-383: what a turn does, as a channel keeps it. A channel with a live connection, such as an MCP tool
/// with a progress token, passes each step on at once, so its client shows activity instead of silence.
/// </summary>
public sealed class SampleAssistantChannelActivity : IProgress<NhAssistantTurnProgress>
{
    private readonly ConcurrentQueue<string> _tools = new();

    /// <summary>
    /// The names of the tools the turn started, in order.
    /// </summary>
    public IReadOnlyList<string> Tools => _tools.ToArray();

    public void Report(NhAssistantTurnProgress value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is { Kind: NhAssistantTurnProgressKinds.ToolStarted, Tool: { } tool })
        {
            _tools.Enqueue(tool.DisplayName);
        }
    }
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
