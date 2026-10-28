using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-268: a messaging channel through which an approver answers an agent proposal with a reply,
/// such as "yes" to the intake reviewer's proposal to activate a project. When the application
/// sends the approver the message about a waiting proposal, it asks the platform whether that person
/// may decide it (<see cref="INhAssistantProposalDecisionService.GetDecisionFactsAsync"/>) and puts a
/// reply token in the message that names the proposal, the person and the proposal hash they saw.
/// The messaging provider delivers the reply to <c>POST approval-replies</c> with that token; the
/// channel vouches for the person because only the token it issued to them can say who answers, and
/// it rebuilds their principal from the user store as their own sign-in would. The platform then
/// decides as that person with the checks of its own decision endpoint, and a reply the provider
/// delivers twice returns the earlier decision.
/// SPM-271: the offer says whether the project changed since the proposal
/// (<see cref="NhAssistantProposalDecisionFacts.SubjectChanged"/>), so the message can say so; the
/// intake reviewer's policy requires an unchanged project, so a "yes" to a changed one is refused with
/// <c>assistant-proposal-stale</c> and closes the proposal.
/// SPM-357: <see cref="OfferToApproversAsync"/> finds whom to ask through the approver directory
/// (<see cref="INhAssistantApproverDirectory.ListForProposalAsync"/>), under the proposal's own policy copy,
/// also one the approval policy selector chose (SPM-356), and says whether the people it asks are everyone
/// who may decide.
/// </summary>
public sealed class SampleApprovalReplyChannel(
    INhAssistantProposalDecisionService decisions,
    INhAssistantApproverDirectory approvers,
    INhUserManager users,
    INhAuthenticationService authentication,
    IDataProtectionProvider dataProtection)
{
    /// <summary>
    /// The channel the platform's audit records for these decisions.
    /// </summary>
    public const string Channel = "messaging-reply";

    /// <summary>
    /// The refusal of a reply whose token this channel did not issue, that expired or whose text is
    /// neither an approval nor a rejection.
    /// </summary>
    public const string ReplyInvalidCode = "sample-approval-reply-invalid";

    /// <summary>
    /// The refusal of a person who may no longer sign in, or who does not exist.
    /// </summary>
    public const string PersonUnavailableCode = "sample-approval-reply-person-unavailable";

    private const string Purpose = "SampleProjectManagement.ApprovalReplies.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITimeLimitedDataProtector _tokens = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>
    /// The reply token for the message that asks <paramref name="userId"/> to decide the proposal, written
    /// in the UI culture the application works in now, as <see cref="OfferAsync(Guid, Guid, CultureInfo, CancellationToken)"/>.
    /// </summary>
    public Task<TaskResult<SampleApprovalReplyOffer>> OfferAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return OfferAsync(approvalId, userId, CultureInfo.CurrentUICulture, cancellationToken);
    }

    /// <summary>
    /// The reply token for the message that asks <paramref name="userId"/> to decide the proposal,
    /// valid until the proposal expires. Fails with the platform's code when the person may not
    /// decide it, such as <c>assistant-four-eyes-required</c> for the person who created the
    /// project; nobody is asked who cannot answer.
    /// SPM-348: the token remembers <paramref name="messageCulture"/>, the language the message asks the
    /// person in, and the reply decides in it (<see cref="NhAssistantDecidingPerson.Culture"/>), so the
    /// order desk later addresses the approver in that language.
    /// </summary>
    public async Task<TaskResult<SampleApprovalReplyOffer>> OfferAsync(
        Guid approvalId,
        Guid userId,
        CultureInfo messageCulture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageCulture);
        if (await PersonAsync(userId, cancellationToken) is not { } person)
        {
            return TaskResult<SampleApprovalReplyOffer>.Failed(PersonUnavailableCode, "The person may not sign in.");
        }

        var facts = await decisions.GetDecisionFactsAsync(person, approvalId, cancellationToken);
        if (!facts.Success)
        {
            return TaskResult<SampleApprovalReplyOffer>.Failed(facts);
        }
        if (!facts.Data!.CanDecide)
        {
            return TaskResult<SampleApprovalReplyOffer>.Failed(
                facts.Data.ReasonCode ?? ReplyInvalidCode,
                "The person may not decide this proposal.");
        }

        var token = _tokens.Protect(
            JsonSerializer.Serialize(new ReplyToken(approvalId, userId, facts.Data.ProposalHash, messageCulture.Name), Json),
            facts.Data.ExpiresAt);
        return TaskResult<SampleApprovalReplyOffer>.Succeeded(new SampleApprovalReplyOffer(token, facts.Data.ExpiresAt, facts.Data.SubjectChanged));
    }

    /// <summary>
    /// SPM-357: the reply tokens for the messages that ask everyone who may decide the proposal now, in
    /// <paramref name="messageCulture"/>. The approver directory lists them under the proposal's policy
    /// copy (four eyes leave out the person who started the work), and each person still gets an offer only
    /// when the platform's own facts say they may decide. <see cref="SampleApprovalReplyOffers.Complete"/>
    /// is <see langword="false"/> when the directory could not list everyone, for example because a policy
    /// rule admits people outside the project; the approval inbox and the agent notifications reach them.
    /// Fails with the platform's code for a proposal that is unknown.
    /// </summary>
    public async Task<TaskResult<SampleApprovalReplyOffers>> OfferToApproversAsync(
        Guid approvalId,
        CultureInfo messageCulture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messageCulture);
        // A host with tenants passes the tenant it works for; the sample has none.
        var listed = await approvers.ListForProposalAsync(approvalId, tenantId: null, cancellationToken: cancellationToken);
        if (!listed.Success)
        {
            return TaskResult<SampleApprovalReplyOffers>.Failed(listed);
        }

        var offers = new List<SampleApproverOffer>();
        foreach (var approver in listed.Data!.Approvers)
        {
            if (!Guid.TryParse(approver.ActorId, out var userId))
            {
                continue;
            }

            var offer = await OfferAsync(approvalId, userId, messageCulture, cancellationToken);
            if (offer.Success)
            {
                offers.Add(new SampleApproverOffer(userId, approver.DisplayName, offer.Data!));
            }
        }

        return TaskResult<SampleApprovalReplyOffers>.Succeeded(new SampleApprovalReplyOffers(offers, listed.Data.Complete));
    }

    /// <summary>
    /// Decides the proposal the reply's token names as the person it names: "yes" or "approve"
    /// approves, "no" or "reject" rejects. The delivery id is the decision's idempotency key, which the
    /// platform records in its audit.
    /// </summary>
    public async Task<TaskResult<NhAssistantProposalDecisionResult>> ReceiveAsync(
        SampleApprovalReply reply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (ReadToken(reply.Token) is not { } token || DecisionOf(reply.Text) is not { } decision)
        {
            return TaskResult<NhAssistantProposalDecisionResult>.Failed(ReplyInvalidCode, "The reply is not an answer to a proposal.");
        }

        // The person may have lost their account since the message was sent.
        if (await PersonAsync(token.UserId, cancellationToken) is not { } person)
        {
            return TaskResult<NhAssistantProposalDecisionResult>.Failed(PersonUnavailableCode, "The person may not sign in.");
        }

        // SPM-348: the person answers in the language of the message; the platform records it with the
        // decision when the application supports it, and a hand-off to the order desk carries it.
        return await decisions.DecideAsync(
            person with { Culture = token.Culture },
            token.ApprovalId,
            token.ProposalHash,
            decision,
            Channel,
            reply.DeliveryId,
            cancellationToken);
    }

    /// <summary>
    /// The person as their own sign-in would authenticate them: an account that may sign in, with
    /// the claims of the user store.
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

    private ReplyToken? ReadToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReplyToken>(_tokens.Unprotect(token), Json);
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
    /// What a reply token names: the proposal, the person, the hash they saw and the culture of the
    /// message that asked them, which a token issued before SPM-348 does not name.
    /// </summary>
    private sealed record ReplyToken(Guid ApprovalId, Guid UserId, string ProposalHash, string? Culture = null);
}

/// <summary>
/// The reply token of a message that asks a person to decide a proposal, until when it is valid, and
/// whether the project changed since the proposal (<see langword="null"/> when the platform cannot tell).
/// </summary>
public sealed record SampleApprovalReplyOffer(string Token, DateTimeOffset ExpiresAt, bool? SubjectChanged = null);

/// <summary>
/// SPM-357: the reply tokens for everyone the channel asks to decide a proposal, and whether they are
/// everyone who may decide it.
/// </summary>
public sealed record SampleApprovalReplyOffers(IReadOnlyList<SampleApproverOffer> Offers, bool Complete);

/// <summary>
/// The message for one approver: who, by the name the project lists them with, and their reply token.
/// </summary>
public sealed record SampleApproverOffer(Guid UserId, string? DisplayName, SampleApprovalReplyOffer Offer);

/// <summary>
/// A reply the messaging provider delivered: its own id, the reply token of the message the person
/// answered and the text of the answer.
/// </summary>
public sealed record SampleApprovalReply(string DeliveryId, string Token, string Text);
