using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-356: the intake review needs four eyes only while a second person may approve it. The intake
/// reviewer's policy (<see cref="SampleIntakeReviewerComposition.ApprovalPolicyId"/>) keeps the person who
/// created the project out, so on a project where that person is the only approver nobody could ever decide
/// the review. The application's approval policy selector asks the approver directory (SPM-357) whether
/// anyone else may approve under that policy; only when the directory is sure nobody else may, the review
/// waits under <see cref="SoleApproverPolicyId"/>, the same policy without four eyes. The choice follows the
/// policy, so it applies to every proposal code resolves to the intake policy, the project planner's too.
/// Every other proposal, and every answer the directory cannot give completely, keeps the policy code
/// resolved.
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the intake policy.
/// </summary>
public static class SampleApprovalPolicySelectionComposition
{
    /// <summary>
    /// The intake policy without four eyes, for a project whose only approver started the review.
    /// </summary>
    public const string SoleApproverPolicyId = "project-intake-sole-approver";

    public static IServiceCollection AddSampleApprovalPolicySelection(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddApprovalPolicy(SoleApproverPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                .Reminders(TimeSpan.FromDays(1))
                .OnExpiry(NhAiApprovalExpiryAction.Withdraw)
                .RequireUnchangedSubject()
                .BindTo(NhAiApprovalBinding.ToolContract))
            // The selector reads and decides; it changes nothing and answers within two seconds.
            .UseApprovalPolicySelector<SampleIntakeApprovalPolicySelector>());
        return services;
    }
}

/// <summary>
/// SPM-356: four eyes on the intake review only while someone other than the person who started it may
/// approve. The proposal keeps the chosen policy, so the decision checks it and a later change of the
/// project's members never changes a waiting review.
/// </summary>
public sealed class SampleIntakeApprovalPolicySelector(INhAssistantApproverDirectory approvers) : INhAssistantApprovalPolicySelector
{
    public async ValueTask<string?> SelectAsync(
        NhAssistantApprovalPolicySelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!string.Equals(selection.ResolvedPolicyId, SampleIntakeReviewerComposition.ApprovalPolicyId, StringComparison.Ordinal)
            || selection.InitiatorActorId is not { } initiator)
        {
            // Not the intake review, or a run nobody started: four eyes keep nobody out.
            return null;
        }

        // One other approver is enough to keep four eyes, so the directory stops at the first.
        var others = await approvers.CountAsync(
            selection.ResolvedPolicyId,
            selection.Subject,
            selection.TenantId,
            excludeActorId: initiator,
            limit: 1,
            cancellationToken);
        if (!others.Success || others.Data!.Count > 0 || !others.Data.Complete)
        {
            // Someone else may approve, or the directory cannot tell: the strict policy stays.
            return null;
        }

        return SampleApprovalPolicySelectionComposition.SoleApproverPolicyId;
    }
}
