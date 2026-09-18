using System.Text.Json;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Builds the conversation read model with message parts hydrated from their authoritative
/// tool-invocation and approval rows.
/// </summary>
internal sealed class NhAssistantConversationReader(INhAssistantStore store)
{
    public const int MaxMessages = 200;

    public async Task<NhAssistantConversationView?> GetAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken)
    {
        var conversation = await store.FindConversationAsync(conversationId, ownerActorId, cancellationToken);
        if (conversation is null)
        {
            return null;
        }
        return await CreateViewAsync(conversation, cancellationToken);
    }

    public async Task<NhAssistantConversationView> CreateViewAsync(
        AssistantConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var messages = await store.GetMessagesAsync(conversation.Id, MaxMessages, cancellationToken);
        var storedParts = messages.ToDictionary(
            message => message.Id,
            message => NhAssistantContent.DeserializeParts(message.PartsJson));
        var invocationIds = storedParts.Values
            .SelectMany(parts => parts)
            .Where(part => part.Type == NhAssistantStoredPart.ToolCallType && part.InvocationId is not null)
            .Select(part => part.InvocationId!.Value)
            .ToHashSet();
        var approvalIds = storedParts.Values
            .SelectMany(parts => parts)
            .Where(part => part.Type == NhAssistantStoredPart.ApprovalType && part.ApprovalId is not null)
            .Select(part => part.ApprovalId!.Value)
            .ToHashSet();
        var invocations = (await store.GetToolInvocationsAsync(conversation.Id, invocationIds, cancellationToken))
            .ToDictionary(invocation => invocation.Id);
        var approvals = (await store.GetApprovalsAsync(conversation.Id, approvalIds, cancellationToken))
            .ToDictionary(approval => approval.Id);

        var messageViews = new List<NhAssistantMessageView>(messages.Count);
        foreach (var message in messages)
        {
            var parts = new List<NhAssistantPartView>();
            foreach (var part in storedParts[message.Id])
            {
                switch (part.Type)
                {
                    case NhAssistantStoredPart.TextType when !string.IsNullOrEmpty(part.Text):
                        parts.Add(new NhAssistantTextPartView(part.Text));
                        break;
                    case NhAssistantStoredPart.ToolCallType
                        when part.InvocationId is { } invocationId && invocations.TryGetValue(invocationId, out var invocation):
                        parts.Add(ToView(invocation));
                        break;
                    case NhAssistantStoredPart.ApprovalType
                        when part.ApprovalId is { } approvalId && approvals.TryGetValue(approvalId, out var approval):
                        parts.Add(ToView(approval));
                        break;
                }
            }
            messageViews.Add(new NhAssistantMessageView(message.Id, message.Role, message.CreatedAt, parts));
        }

        NhAssistantApprovalView? pending = null;
        if (conversation.Status == NhAssistantConversationStatuses.WaitingForApproval)
        {
            var pendingApproval = await store.FindPendingApprovalAsync(conversation.Id, cancellationToken);
            pending = pendingApproval is null ? null : ToView(pendingApproval);
        }

        return new NhAssistantConversationView(
            conversation.Id,
            conversation.AgentId,
            conversation.AgentVersion,
            conversation.Title,
            conversation.Status,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messageViews,
            pending);
    }

    public static NhAssistantToolCallPartView ToView(AssistantToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new NhAssistantToolCallPartView(
            invocation.Id,
            invocation.ToolId,
            invocation.ToolVersion,
            invocation.DisplayName,
            invocation.Status,
            NhAssistantContent.Preview(invocation.ArgumentsJson, invocation.DataClassification),
            NhAssistantContent.ResultPreview(invocation.ResultJson, invocation.DataClassification),
            invocation.ResultCode);
    }

    public static NhAssistantApprovalView ToView(AssistantApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var status = approval.Status == NhAssistantApprovalStatuses.Pending && approval.ExpiresAt <= DateTimeOffset.UtcNow
            ? NhAssistantApprovalStatuses.Expired
            : approval.Status;
        var targets = JsonSerializer.Deserialize<string[]>(approval.TargetsJson, NhAssistantContent.JsonOptions) ?? [];
        return new NhAssistantApprovalView(
            approval.Id,
            approval.ProposalId,
            approval.ProposalHash,
            approval.ToolId,
            approval.Summary,
            approval.ArgumentsPreview,
            targets,
            approval.ExpiresAt,
            status);
    }
}
