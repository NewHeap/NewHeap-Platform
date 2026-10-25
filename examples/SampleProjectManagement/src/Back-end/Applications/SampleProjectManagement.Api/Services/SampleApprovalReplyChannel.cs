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
/// </summary>
public sealed class SampleApprovalReplyChannel(
    INhAssistantProposalDecisionService decisions,
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
    /// The reply token for the message that asks <paramref name="userId"/> to decide the proposal,
    /// valid until the proposal expires. Fails with the platform's code when the person may not
    /// decide it, such as <c>assistant-four-eyes-required</c> for the person who created the
    /// project; nobody is asked who cannot answer.
    /// </summary>
    public async Task<TaskResult<SampleApprovalReplyOffer>> OfferAsync(
        Guid approvalId,
        Guid userId,
        CancellationToken cancellationToken)
    {
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
            JsonSerializer.Serialize(new ReplyToken(approvalId, userId, facts.Data.ProposalHash), Json),
            facts.Data.ExpiresAt);
        return TaskResult<SampleApprovalReplyOffer>.Succeeded(new SampleApprovalReplyOffer(token, facts.Data.ExpiresAt, facts.Data.SubjectChanged));
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

        return await decisions.DecideAsync(
            person,
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
        return new NhAssistantDecidingPerson(principal, userId.ToString("D"));
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

    private sealed record ReplyToken(Guid ApprovalId, Guid UserId, string ProposalHash);
}

/// <summary>
/// The reply token of a message that asks a person to decide a proposal, until when it is valid, and
/// whether the project changed since the proposal (<see langword="null"/> when the platform cannot tell).
/// </summary>
public sealed record SampleApprovalReplyOffer(string Token, DateTimeOffset ExpiresAt, bool? SubjectChanged = null);

/// <summary>
/// A reply the messaging provider delivered: its own id, the reply token of the message the person
/// answered and the text of the answer.
/// </summary>
public sealed record SampleApprovalReply(string DeliveryId, string Token, string Text);
