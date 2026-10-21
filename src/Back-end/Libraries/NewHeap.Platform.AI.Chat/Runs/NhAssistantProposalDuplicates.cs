using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The outcome of the duplicate check before a proposal is recorded: an existing proposal that
/// makes the new one unnecessary, or the pending proposals the new one replaces.
/// </summary>
internal sealed record NhAssistantDuplicateCheck(
    NhAssistantProposeOutcome Outcome,
    AssistantApproval? Existing,
    IReadOnlyList<AssistantApproval> Superseded)
{
    public static NhAssistantDuplicateCheck None { get; } = new(NhAssistantProposeOutcome.Created, null, []);
}

/// <summary>
/// Keeps agent proposals from piling up: an identical open proposal is never recorded twice, a
/// newer proposal for the same objects follows the policy's <see cref="NhAiDuplicateProposals"/>,
/// and a proposal people rejected recently is not made again. Checks the proposals of one agent
/// within one scope key. The rejection cooldown counts on the application's clock (a registered
/// <see cref="TimeProvider"/>, otherwise the system clock), on which decisions are recorded.
/// </summary>
internal sealed class NhAssistantProposalDuplicates(NhAssistantRunStore runs, TimeProvider? timeProvider = null)
{
    private static readonly string[] OpenStatuses =
    [
        NhAssistantApprovalStatuses.Pending,
        NhAssistantApprovalStatuses.Approved,
        NhAssistantApprovalStatuses.Executing
    ];

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The identity of a proposal: its tool id and canonical arguments.
    /// </summary>
    public static string IdentityOf(string toolId, JsonElement canonicalArguments)
    {
        var bytes = Encoding.UTF8.GetBytes(toolId + "\n" + canonicalArguments.GetRawText());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public async Task<NhAssistantDuplicateCheck> CheckAsync(
        AssistantThread thread,
        string toolId,
        string argumentsHash,
        IReadOnlyCollection<NhAiProposalKey> keys,
        NhAiApprovalPolicy policy,
        CancellationToken cancellationToken)
    {
        if (await runs.FindOpenProposalAsync(thread.ScopeKey, thread.AgentId, toolId, argumentsHash, cancellationToken) is { } open)
        {
            return new NhAssistantDuplicateCheck(NhAssistantProposeOutcome.AlreadyOpen, open, []);
        }

        if (policy.RejectionCooldown > TimeSpan.Zero)
        {
            var since = _applicationClock.GetUtcNow() - policy.RejectionCooldown;
            var rejected = await runs.FindRecentRejectionAsync(thread.ScopeKey, thread.AgentId, toolId, argumentsHash, since, cancellationToken)
                ?? (await runs.FindProposalsForObjectsAsync(
                    thread.ScopeKey,
                    thread.AgentId,
                    toolId,
                    keys,
                    [NhAssistantApprovalStatuses.Rejected],
                    since,
                    cancellationToken)).FirstOrDefault();
            if (rejected is not null)
            {
                return new NhAssistantDuplicateCheck(NhAssistantProposeOutcome.RecentlyRejected, rejected, []);
            }
        }

        if (keys.Count == 0 || policy.Duplicates == NhAiDuplicateProposals.Allow)
        {
            return NhAssistantDuplicateCheck.None;
        }

        var sameObjects = await runs.FindProposalsForObjectsAsync(
            thread.ScopeKey,
            thread.AgentId,
            toolId,
            keys,
            OpenStatuses,
            null,
            cancellationToken);
        // An approved proposal is about to change the objects; a newer one waits until it ran.
        if (sameObjects.FirstOrDefault(approval => approval.Status != NhAssistantApprovalStatuses.Pending) is { } underway)
        {
            return new NhAssistantDuplicateCheck(NhAssistantProposeOutcome.AlreadyOpen, underway, []);
        }
        if (sameObjects.Count == 0)
        {
            return NhAssistantDuplicateCheck.None;
        }

        return policy.Duplicates == NhAiDuplicateProposals.Skip
            ? new NhAssistantDuplicateCheck(NhAssistantProposeOutcome.AlreadyOpen, sameObjects[0], [])
            : NhAssistantDuplicateCheck.None with { Superseded = sameObjects };
    }
}
