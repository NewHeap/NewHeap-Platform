using System.Security.Claims;
using System.Text.RegularExpressions;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Decides proposals of agent threads for the application's own channels, such as a reply to a
/// message that asked an approver to decide. It is a server API for the application's code; there
/// is no endpoint for it. The application vouches for the person: it authenticated them through its
/// channel (for example a reply token it issued to that person) and passes their principal and actor
/// id, as the assistant's context resolver gives them for the person's own requests. Everything else
/// is the decision of <c>POST approvals/{approvalId}/decisions</c>, through one implementation: the
/// approval policy's approvers and the tool's approve policy, four eyes, the proposal hash, the proposal
/// guards and <c>RequireUnchangedSubject</c>, the expiry on the application's clock, what follows the
/// decision (execution, the waiting run, the follow-up run and the rejection cooldown) and the
/// content-free audit, which records the channel and the idempotency key. Proposals that a person decides
/// in a conversation (their own chat turn, or a consultation the asker decides) are refused here as
/// there.
/// </summary>
public interface INhAssistantProposalDecisionService
{
    /// <summary>
    /// What a decision of <paramref name="person"/> would meet now, without proposal content: the
    /// current proposal hash, the expiry, whether four eyes apply, how many approvals the proposal
    /// still needs and whether the person may decide it, with the refusal code otherwise. Nothing
    /// changes: an expired proposal is not closed and the proposal guards do not run. Fails with
    /// <c>assistant-approval-not-found</c> for an unknown proposal or one the person may not see.
    /// </summary>
    Task<TaskResult<NhAssistantProposalDecisionFacts>> GetDecisionFactsAsync(
        NhAssistantDecidingPerson person,
        Guid approvalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides the proposal as <paramref name="person"/> through <paramref name="channel"/>, with the
    /// checks and effects of <c>POST approvals/{approvalId}/decisions</c>.
    /// <para>
    /// A repeat by the same person with the same decision and hash, such as a channel that delivers a
    /// reply twice, changes nothing and returns the earlier decision with
    /// <see cref="NhAssistantProposalDecisionResult.Duplicate"/>; it is not audited again. Any other
    /// decision of a decided proposal fails with <c>assistant-approval-not-pending</c>.
    /// </para>
    /// <para>
    /// Fails with the endpoint's codes: <c>assistant-approval-not-found</c>,
    /// <c>assistant-proposal-hash-mismatch</c>, <c>assistant-approval-not-pending</c>,
    /// <c>assistant-approval-expired</c>, <c>assistant-four-eyes-required</c>,
    /// <c>assistant-approval-policy-denied</c>, <c>assistant-proposal-stale</c>,
    /// <c>assistant-proposal-check-failed</c>, <c>assistant-consult-asker-decides</c>, and
    /// <c>assistant-approval-decision-invalid</c> for a proposal decided in a conversation, an agent
    /// as the person or an idempotency key that is not usable.
    /// </para>
    /// </summary>
    /// <param name="person">The person the application authenticated.</param>
    /// <param name="approvalId">The proposal.</param>
    /// <param name="expectedProposalHash">The hash the person saw, from
    /// <see cref="GetDecisionFactsAsync"/> or a proposal the person read; a proposal that changed since
    /// is refused.</param>
    /// <param name="decision">Approve or reject.</param>
    /// <param name="channel">The application's channel, a dash-case code of at most
    /// <see cref="NhAssistantDecisionChannels.MaxLength"/> characters such as <c>messaging-reply</c>,
    /// recorded in the audit; the decision endpoints record <see cref="NhAssistantDecisionChannels.Web"/>.</param>
    /// <param name="idempotencyKey">The application's key of the delivery that decided, such as the
    /// id of the reply: at most <see cref="NhAssistantDecisionChannels.MaxIdempotencyKeyLength"/>
    /// printable characters without white space. Only the audit records it; whether a decision
    /// repeats another follows from the proposal's state, not from the key.</param>
    Task<TaskResult<NhAssistantProposalDecisionResult>> DecideAsync(
        NhAssistantDecidingPerson person,
        Guid approvalId,
        string expectedProposalHash,
        NhAssistantProposalDecision decision,
        string channel,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The person who decides through <see cref="INhAssistantProposalDecisionService"/>, as the
/// application authenticated them: their principal, which the approve policies are evaluated against,
/// and their actor id, which approval policies, subject roles and four eyes compare.
/// </summary>
/// <param name="User">The person's principal with the claims of their own sign-in.</param>
/// <param name="ActorId">The person's actor id, as the assistant's context resolver gives it for the
/// person's own requests.</param>
/// <param name="TenantId">The person's tenant, as the context resolver gives it; <see langword="null"/>
/// without tenants. Decisions apply the same tenant rules as the decision endpoints.</param>
public sealed record NhAssistantDecidingPerson(ClaimsPrincipal User, string ActorId, string? TenantId = null);

/// <summary>
/// A decision of a proposal.
/// </summary>
public enum NhAssistantProposalDecision
{
    Approve = 0,
    Reject = 1
}

/// <summary>
/// What a decision of a person would meet now (<see cref="INhAssistantProposalDecisionService.GetDecisionFactsAsync"/>),
/// without proposal content.
/// </summary>
/// <param name="ApprovalId">The proposal.</param>
/// <param name="Status">Its status, one of <see cref="NhAssistantApprovalStatuses"/>; a pending proposal
/// past its expiry still reads pending until a decision or the maintenance job closes it.</param>
/// <param name="ProposalHash">The current proposal hash, which a decision passes back.</param>
/// <param name="ExpiresAt">When the proposal expires, on the application's clock.</param>
/// <param name="FourEyes">Whether the approval policy keeps the person who started the work out.</param>
/// <param name="ApprovalsNeeded">How many approvals the proposal still needs: one while it waits for a
/// decision before its expiry, otherwise zero. One approver decides a proposal; four eyes only says who
/// that may be.</param>
/// <param name="CanDecide">Whether the person may decide it now. The proposal guards run only when a
/// person approves, so an approval may still be refused as stale.</param>
/// <param name="ReasonCode">Why the person may not decide it, with the code a decision would fail
/// with, such as <c>assistant-four-eyes-required</c>; <see langword="null"/> when they may.</param>
public sealed record NhAssistantProposalDecisionFacts(
    Guid ApprovalId,
    string Status,
    string ProposalHash,
    DateTimeOffset ExpiresAt,
    bool FourEyes,
    int ApprovalsNeeded,
    bool CanDecide,
    string? ReasonCode);

/// <summary>
/// The decision <see cref="INhAssistantProposalDecisionService.DecideAsync"/> recorded, or the
/// person's earlier one that it repeated.
/// </summary>
/// <param name="ApprovalId">The proposal.</param>
/// <param name="Status">Its status after the decision, one of <see cref="NhAssistantApprovalStatuses"/>;
/// for a repeat its current status, such as <c>executed</c>.</param>
/// <param name="Decision">The decision.</param>
/// <param name="DecidedAt">When the person decided, on the application's clock.</param>
/// <param name="Duplicate">The person had already decided the proposal so with this hash; nothing
/// changed and nothing was audited again.</param>
public sealed record NhAssistantProposalDecisionResult(
    Guid ApprovalId,
    string Status,
    NhAssistantProposalDecision Decision,
    DateTimeOffset? DecidedAt,
    bool Duplicate);

/// <summary>
/// The channels through which proposals are decided, as the audit records them
/// (<see cref="NhAssistantAuditEvent.Channel"/>).
/// </summary>
public static partial class NhAssistantDecisionChannels
{
    /// <summary>
    /// The decision endpoints of the assistant, <c>POST approvals/{approvalId}/decisions</c> and
    /// <c>POST approvals/decisions</c>.
    /// </summary>
    public const string Web = "web";

    /// <summary>
    /// The most characters of a channel.
    /// </summary>
    public const int MaxLength = 50;

    /// <summary>
    /// The most characters of an idempotency key of a decision.
    /// </summary>
    public const int MaxIdempotencyKeyLength = 100;

    /// <summary>
    /// Whether <paramref name="channel"/> is a channel: a dash-case code of at most
    /// <see cref="MaxLength"/> characters, such as <c>messaging-reply</c>.
    /// </summary>
    public static bool IsValid(string? channel)
    {
        return !string.IsNullOrEmpty(channel)
            && channel.Length <= MaxLength
            && ChannelPattern().IsMatch(channel);
    }

    /// <summary>
    /// Whether <paramref name="idempotencyKey"/> may be recorded: absent, or at most
    /// <see cref="MaxIdempotencyKeyLength"/> printable ASCII characters without white space.
    /// </summary>
    public static bool IsValidIdempotencyKey(string? idempotencyKey)
    {
        return idempotencyKey is null
            || (idempotencyKey.Length is > 0 and <= MaxIdempotencyKeyLength
                && idempotencyKey.All(character => character is > ' ' and < '\u007F'));
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
}

/// <summary>
/// The decision service over the decision the endpoints use (<see cref="NhAssistantProposalDecisions"/>).
/// </summary>
internal sealed class NhAssistantProposalDecisionService(NhAssistantProposalDecisions decisions) : INhAssistantProposalDecisionService
{
    public async Task<TaskResult<NhAssistantProposalDecisionFacts>> GetDecisionFactsAsync(
        NhAssistantDecidingPerson person,
        Guid approvalId,
        CancellationToken cancellationToken = default)
    {
        ValidatePerson(person);

        return await decisions.FactsAsync(approvalId, person.ActorId, person.User, cancellationToken);
    }

    public async Task<TaskResult<NhAssistantProposalDecisionResult>> DecideAsync(
        NhAssistantDecidingPerson person,
        Guid approvalId,
        string expectedProposalHash,
        NhAssistantProposalDecision decision,
        string channel,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePerson(person);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProposalHash);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, "A decision approves or rejects.");
        }
        if (!NhAssistantDecisionChannels.IsValid(channel))
        {
            throw new ArgumentException(
                $"A decision channel is a dash-case code of at most {NhAssistantDecisionChannels.MaxLength} characters, such as messaging-reply.",
                nameof(channel));
        }
        // The key often comes from the channel's own delivery, so an unusable one is an expected
        // refusal rather than a programming error.
        if (!NhAssistantDecisionChannels.IsValidIdempotencyKey(idempotencyKey))
        {
            return TaskResult<NhAssistantProposalDecisionResult>.Failed(
                NhAssistantErrorCodes.ApprovalDecisionInvalid,
                "The idempotency key of the decision is not usable.");
        }

        var approve = decision == NhAssistantProposalDecision.Approve;
        var decided = await decisions.DecideAsync(
            approvalId,
            person.ActorId,
            person.User,
            approve,
            expectedProposalHash,
            reason: null,
            new NhAssistantDecisionOrigin(channel, idempotencyKey),
            cancellationToken);
        if (decided.Success)
        {
            return TaskResult<NhAssistantProposalDecisionResult>.Succeeded(ResultOf(decided.Data!, decision, duplicate: false));
        }

        // Idempotent by state: a decided proposal the person decided so before is the earlier
        // decision; every other decision of it keeps the ordinary refusal.
        if (NhAssistantResults.CodeOf(decided) == NhAssistantErrorCodes.ApprovalNotPending
            && await decisions.FindRepeatAsync(approvalId, person.ActorId, approve, expectedProposalHash, cancellationToken) is { } earlier)
        {
            return TaskResult<NhAssistantProposalDecisionResult>.Succeeded(ResultOf(earlier, decision, duplicate: true));
        }

        return TaskResult<NhAssistantProposalDecisionResult>.Failed(decided);
    }

    private static void ValidatePerson(NhAssistantDecidingPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(person.User, nameof(person));
        ArgumentException.ThrowIfNullOrWhiteSpace(person.ActorId, nameof(person));
    }

    private static NhAssistantProposalDecisionResult ResultOf(
        AssistantApproval approval,
        NhAssistantProposalDecision decision,
        bool duplicate)
    {
        return new NhAssistantProposalDecisionResult(approval.Id, approval.Status, decision, approval.DecidedAt, duplicate);
    }
}
