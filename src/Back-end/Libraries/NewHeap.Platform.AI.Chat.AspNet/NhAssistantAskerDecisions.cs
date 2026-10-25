using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The proposal of a consultation that a person decides in their own conversation as the person who
/// asked, for the decide endpoint and <see cref="INhAssistantConversationTurns"/> alike.
/// </summary>
internal static class NhAssistantAskerDecisions
{
    /// <summary>
    /// The consultation's proposal <paramref name="actorId"/> decides in the conversation, or none for
    /// the conversation's own proposals. An approval is checked again with <paramref name="user"/>
    /// against the consulted agent's thread (CP4), as any approver is. Returns the refusal code
    /// otherwise.
    /// </summary>
    public static async Task<(AssistantApproval? Approval, string? ErrorCode)> FindAsync(
        IServiceProvider services,
        Guid conversationId,
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        bool approve,
        CancellationToken cancellationToken)
    {
        var approval = await services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, cancellationToken);
        if (approval is null
            || approval.ConversationId == conversationId
            || approval.ConsultationId is null
            || approval.Mode != NhAssistantApprovalModes.Inline)
        {
            return (null, null);
        }

        var runs = services.GetRequiredService<NhAssistantRunStore>();
        var row = (await runs.GetAskerDecisionsAsync(conversationId, [approval.Id], cancellationToken)).FirstOrDefault();
        if (row is null)
        {
            return (null, NhAssistantErrorCodes.ApprovalNotFound);
        }
        if (!string.Equals(approval.InitiatorActorId, actorId, StringComparison.Ordinal))
        {
            return (null, NhAssistantErrorCodes.ApprovalForbidden);
        }
        if (approve)
        {
            var check = await services.GetRequiredService<NhAssistantApproverCheck>().CheckAsync(
                approval,
                row.Thread,
                actorId,
                user,
                cancellationToken);
            if (!check.Success)
            {
                return (null, NhAssistantErrorCodes.ApprovalPolicyDenied);
            }
        }

        return (approval, null);
    }
}
