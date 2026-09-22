using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Governance;

/// <summary>
/// Supplies approval evidence from <c>AssistantApproval</c>. Evidence is returned only for an
/// approved decision whose stored proposal still hashes to the recorded proposal hash, that belongs
/// to the invoking actor and accountable owner, and that was decided by a different actor. During an
/// assistant tool call without evidence it captures the exact arguments and context the invoker
/// evaluated, so the turn runner can create the proposal from them. Other requests are delegated to
/// the evidence provider that was registered before the assistant.
/// </summary>
internal sealed class NhAssistantApprovalEvidenceProvider(
    NhAssistantDbContextFactory contextFactory,
    INhAiProposalFactory proposalFactory,
    [FromKeyedServices(NhAssistantFallbacks.ServiceKey)] INhAiApprovalEvidenceProvider fallback)
    : INhAiApprovalEvidenceProvider
{
    public async ValueTask<NhAiApprovalEvidence?> GetAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        if (Guid.TryParse(context.ProposalId, out var proposalId)
            && Guid.TryParse(context.ApprovalId, out var approvalId))
        {
            var stored = await FindApprovedAsync(proposalId, approvalId, cancellationToken);
            if (stored is not null)
            {
                return CreateEvidence(stored, context);
            }
        }

        var call = NhAssistantExecutionScope.CurrentCall;
        if (call is not null)
        {
            call.CaptureApprovalRequest(arguments, context);
            return null;
        }

        return await fallback.GetAsync(descriptor, context, arguments, cancellationToken);
    }

    private async Task<Entities.AssistantApproval?> FindApprovedAsync(
        Guid proposalId,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .SingleOrDefaultAsync(
                approval => approval.Id == approvalId
                    && approval.ProposalId == proposalId
                    && approval.Status == NhAssistantApprovalStatuses.Approved,
                cancellationToken);
    }

    private NhAiApprovalEvidence? CreateEvidence(
        Entities.AssistantApproval stored,
        NhAiInvocationContext context)
    {
        var proposal = NhAssistantProposalSerializer.Deserialize(stored.ProposalJson);
        if (proposal.ProposalId != stored.ProposalId
            || !string.Equals(proposal.ProposalHash, stored.ProposalHash, StringComparison.Ordinal)
            || !string.Equals(proposalFactory.ComputeHash(proposal), stored.ProposalHash, StringComparison.Ordinal)
            || !string.Equals(proposal.ActorId, context.ActorId, StringComparison.Ordinal)
            || !string.Equals(proposal.AccountableOwnerId, context.AccountableOwnerId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(stored.DecidedByActorId)
            || string.Equals(stored.DecidedByActorId, proposal.ActorId, StringComparison.Ordinal)
            || stored.DecidedAt is not { } decidedAt
            || stored.ApprovalExpiresAt is not { } approvalExpiresAt)
        {
            return null;
        }

        return new NhAiApprovalEvidence(
            proposal,
            new NhAiApproval(
                stored.Id,
                proposal.ProposalId,
                proposal.ProposalHash,
                stored.DecidedByActorId,
                proposal.Targets,
                proposal.Constraints,
                decidedAt,
                approvalExpiresAt,
                proposal.EstimatedBudget));
    }
}

/// <summary>
/// Serializes NewHeap proposals for storage with deterministic web defaults.
/// </summary>
internal static class NhAssistantProposalSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(NhAiProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return JsonSerializer.Serialize(proposal, Options);
    }

    public static NhAiProposal Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonSerializer.Deserialize<NhAiProposal>(json, Options)
                ?? throw new InvalidOperationException("A stored assistant proposal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("A stored assistant proposal is corrupt.", exception);
        }
    }
}
