using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The caller's standing in an agent thread for the conversation contract: the subject, the live
/// role and, for every open proposal, whether the caller may decide it now. In a thread the
/// subject's approvers decide, not the author of a turn, so the assistant panel needs this to offer
/// the decision to the right people.
/// </summary>
internal static class NhAssistantThreadStanding
{
    /// <summary>
    /// At most this many open proposals are checked per conversation read.
    /// </summary>
    public const int MaxDecisions = 50;

    public static async Task<NhAssistantConversationThreadDto> CreateAsync(
        IServiceProvider services,
        ClaimsPrincipal user,
        NhAssistantThreadAccess access,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

        var thread = access.Thread;
        var approvals = await services.GetRequiredService<NhAssistantRunStore>()
            .GetOpenThreadApprovalsAsync(thread.ConversationId, MaxDecisions, cancellationToken);
        var approvers = services.GetRequiredService<NhAssistantApproverCheck>();

        var decisions = new List<NhAssistantThreadDecisionDto>(approvals.Count);
        foreach (var approval in approvals)
        {
            // The same live check as the decision itself: policy approvers, four-eyes and the
            // tool's approve policy.
            var check = await approvers.CheckAsync(approval, thread, actorId, user, cancellationToken);
            decisions.Add(new NhAssistantThreadDecisionDto(
                approval.Id,
                approval.RunId,
                check.Success,
                check.Success ? null : ReasonCode(check)));
        }

        return new NhAssistantConversationThreadDto(
            thread.SubjectType,
            thread.SubjectId,
            thread.SubjectDisplayName,
            NhAssistantAgentRunDtoMapper.RoleName(access.Role),
            decisions);
    }

    private static string ReasonCode(TaskResult check)
    {
        return check.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? NhAssistantErrorCodes.ApprovalPolicyDenied;
    }
}
