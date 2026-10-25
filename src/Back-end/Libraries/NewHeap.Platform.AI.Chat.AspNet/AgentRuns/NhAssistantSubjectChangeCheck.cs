using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The outcome of the subject check of a proposal: the precondition's result and whether it could
/// tell at all.
/// </summary>
/// <param name="Result">What the precondition answered.</param>
/// <param name="Known">Whether the answer says something: <see langword="false"/> when the default
/// precondition had no recorded or no current subject version to compare, so it could only answer
/// satisfied.</param>
internal sealed record NhAssistantSubjectChange(NhAiPreconditionResult Result, bool Known)
{
    /// <summary>
    /// Whether the subject changed: <see langword="true"/> for a stale result, <see langword="false"/>
    /// for a satisfied one, and <see langword="null"/> when the precondition could not tell.
    /// </summary>
    public bool? Changed => Known ? Result.Outcome == NhAiPreconditionOutcome.Stale : null;
}

/// <summary>
/// The precondition that an approved proposal meets right before its execution
/// (<see cref="INhAiProposalPrecondition"/>, by default <see cref="NhAssistantSubjectVersionPrecondition"/>),
/// asked with the proposal's recorded subject version and the subject's version now. The execution, the
/// decision facts and the decision run the same check, so a decision refuses what the execution would
/// close as stale.
/// </summary>
internal static class NhAssistantSubjectChangeCheck
{
    public static async Task<NhAssistantSubjectChange> CheckAsync(
        INhAiProposalPrecondition precondition,
        NhAssistantSubjectAccessService subjects,
        AssistantApproval approval,
        NhAiProposal proposal,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(subject);

        var current = await subjects.DescribeAsync(subject, cancellationToken);
        var context = new NhAiProposalPreconditionContext(
            proposal.ProposalId,
            proposal.ToolId,
            proposal.Arguments,
            subject,
            approval.SubjectVersion,
            current?.Version);
        var result = await precondition.CheckAsync(context, cancellationToken);

        // The default precondition compares versions; without both it answers satisfied but cannot tell.
        var known = precondition is not NhAssistantSubjectVersionPrecondition
            || (context.RecordedSubjectVersion is not null && context.CurrentSubjectVersion is not null);
        return new NhAssistantSubjectChange(result, known);
    }
}
