using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Api.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-356: the intake review needs four eyes only while a second person may approve it. The intake
/// reviewer's policy (<see cref="SampleIntakeReviewerComposition.ApprovalPolicyId"/>) keeps the person who
/// created the project out, so on a project where that person is the only approver nobody could ever decide
/// the review. The application's approval policy selector asks the approver directory (SPM-357) whether
/// anyone else may approve under that policy. SPM-378: when nobody else approves the project, the
/// application's project managers decide the review, still four-eyes, under
/// <see cref="PortfolioPolicyId"/>, whose policy rule the directory can count completely because the
/// application names its project managers (<see cref="SampleApproverCandidates"/>). Only when the directory
/// is sure nobody else may approve at all, the review waits under <see cref="SoleApproverPolicyId"/>, the
/// same policy without four eyes. The choice follows the policy, so it applies to every proposal code
/// resolves to the intake policy, the project planner's too. Every other proposal, and every answer the
/// directory cannot give completely, keeps the policy code resolved. SPM-379: a review that waits without
/// four eyes closes at its decision once someone else may approve after all
/// (<see cref="SampleSoleApproverProposalGuard"/>).
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the intake policy.
/// </summary>
public static class SampleApprovalPolicySelectionComposition
{
    /// <summary>
    /// The intake policy without four eyes, for a project whose only approver started the review.
    /// </summary>
    public const string SoleApproverPolicyId = "project-intake-sole-approver";

    /// <summary>
    /// SPM-378: the intake policy with the application's project managers as approvers too, through a
    /// policy rule, for a project whose only approver started the review.
    /// </summary>
    public const string PortfolioPolicyId = "project-intake-portfolio";

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
            .AddApprovalPolicy(PortfolioPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                // The project's approvers and anyone who may manage the application's projects.
                .Approvers(
                    NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver),
                    NhAiApprovers.Policy(SampleProjectStarterComposition.ProjectManagePolicy))
                .FourEyes()
                .Reminders(TimeSpan.FromDays(1))
                .OnExpiry(NhAiApprovalExpiryAction.Withdraw)
                .RequireUnchangedSubject()
                .BindTo(NhAiApprovalBinding.ToolContract))
            // SPM-378: the application names who passes its policy rules, so the directory knows when
            // nobody else may approve.
            .UseApproverCandidates<SampleApproverCandidates>()
            // The selector reads and decides; it changes nothing and answers within two seconds.
            .UseApprovalPolicySelector<SampleIntakeApprovalPolicySelector>()
            // SPM-379: the guard reads the policy each proposal recorded, at revalidation and decision.
            .AddProposalGuard<SampleSoleApproverProposalGuard>(
                ["projects.*", "project-tasks.*"],
                NhAiProposalGuardStages.Revalidation | NhAiProposalGuardStages.Decision));
        return services;
    }
}

/// <summary>
/// SPM-356 and SPM-378: four eyes on the intake review only while someone other than the person who started
/// it may approve. The proposal keeps the chosen policy, so the decision checks it and a later change of the
/// project's members never changes the policy of a waiting review.
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
            // Someone else on the project may approve, or the directory cannot tell: the strict policy stays.
            return null;
        }

        // SPM-378: nobody else on the project. A project manager of the application decides then, four-eyes.
        // The portfolio policy admits them through a policy rule, which the directory counts completely only
        // because the application names its project managers.
        var managers = await approvers.CountAsync(
            SampleApprovalPolicySelectionComposition.PortfolioPolicyId,
            selection.Subject,
            selection.TenantId,
            excludeActorId: initiator,
            limit: 1,
            cancellationToken);
        if (!managers.Success)
        {
            return null;
        }
        if (managers.Data!.Count > 0)
        {
            return SampleApprovalPolicySelectionComposition.PortfolioPolicyId;
        }

        // Nobody else may approve at all, and the directory is sure of it.
        return managers.Data.Complete ? SampleApprovalPolicySelectionComposition.SoleApproverPolicyId : null;
    }
}

/// <summary>
/// SPM-379: a review that waits without four eyes (<see cref="SampleApprovalPolicySelectionComposition.SoleApproverPolicyId"/>)
/// no longer fits once someone besides the person who started it may approve, for example a colleague who
/// joined the project as approver while the review waited. The selector chose the policy once, when the
/// review was proposed; the guard reads that recorded choice (<see cref="NhAiProposalGuardContext.PolicyId"/>)
/// at revalidation and at the decision, counts again who may approve under the portfolio policy and closes
/// the review when two people may: at most one of them started it. The reviewer proposes again on its next
/// run, four-eyes. A review under another policy, or a count the directory cannot give, is left as it is.
/// Not at execution: an approved review executes as approved.
/// </summary>
public sealed class SampleSoleApproverProposalGuard(
    INhAssistantApproverDirectory approvers,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAiProposalGuard
{
    public const string SecondApproverCode = "project-intake-second-approver";

    public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
    }

    public async ValueTask<NhAiPreconditionResult> CheckAsync(
        NhAiProposalGuardContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(context.PolicyId, SampleApprovalPolicySelectionComposition.SoleApproverPolicyId, StringComparison.Ordinal)
            || context.Subject is not { } subject)
        {
            return NhAiPreconditionResult.Satisfied;
        }

        // The sample has no tenants.
        var now = await approvers.CountAsync(
            SampleApprovalPolicySelectionComposition.PortfolioPolicyId,
            subject,
            tenantId: null,
            limit: 2,
            cancellationToken: cancellationToken);
        if (!now.Success || now.Data!.Count < 2)
        {
            return NhAiPreconditionResult.Satisfied;
        }

        return NhAiPreconditionResult.NotApplicable(SecondApproverCode, _ => localizer["StaleIntakeSecondApprover"].Value);
    }
}
