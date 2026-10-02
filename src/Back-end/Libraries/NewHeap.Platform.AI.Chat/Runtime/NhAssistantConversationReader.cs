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

    /// <summary>
    /// Returns the conversation as the owner or a participant of the same tenant sees it.
    /// </summary>
    public async Task<NhAssistantConversationView?> GetAsync(
        Guid conversationId,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var access = await store.FindAccessAsync(conversationId, actorId, tenantId, cancellationToken);
        if (access is null)
        {
            return null;
        }
        return await CreateViewAsync(access, cancellationToken);
    }

    /// <summary>
    /// Returns the conversation as its owner sees it.
    /// </summary>
    public Task<NhAssistantConversationView> CreateViewAsync(
        AssistantConversation conversation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return CreateViewAsync(
            new NhAssistantConversationAccess(conversation, conversation.OwnerActorId, null),
            cancellationToken);
    }

    public async Task<NhAssistantConversationView> CreateViewAsync(
        NhAssistantConversationAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var conversation = access.Conversation;
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
            messageViews.Add(new NhAssistantMessageView(message.Id, message.Role, message.CreatedAt, parts)
            {
                Sequence = message.Sequence,
                AuthorActorId = message.Role == NhAssistantMessageRoles.User
                    ? message.AuthorActorId ?? conversation.OwnerActorId
                    : null
            });
        }

        NhAssistantApprovalView? pending = null;
        if (conversation.Status == NhAssistantConversationStatuses.WaitingForApproval)
        {
            var pendingApproval = await store.FindPendingApprovalAsync(conversation.Id, cancellationToken);
            pending = pendingApproval is null ? null : ToView(pendingApproval);
        }

        var participants = await store.GetParticipantsAsync(conversation.Id, cancellationToken);
        IReadOnlyList<NhAssistantMemberView> members = participants.Count == 0
            ? []
            :
            [
                new NhAssistantMemberView(
                    conversation.OwnerActorId,
                    conversation.OwnerDisplayName ?? string.Empty,
                    NhAssistantParticipantRoles.Owner,
                    conversation.CreatedAt),
                .. participants.Select(participant => new NhAssistantMemberView(
                    participant.ActorId,
                    participant.DisplayName,
                    NhAssistantParticipantRoles.Participant,
                    participant.JoinedAt))
            ];
        var lastMessageSequence = messages.Count == 0 ? 0 : messages.Max(message => message.Sequence);

        return new NhAssistantConversationView(
            conversation.Id,
            conversation.AgentId,
            conversation.AgentVersion,
            conversation.Title,
            conversation.Status,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messageViews,
            pending)
        {
            Role = access.Role,
            LastReadSequence = access.LastReadSequence ?? lastMessageSequence,
            LastMessageSequence = lastMessageSequence,
            ActiveActorId = access.ActiveActorId,
            Members = members,
            ProtectedShareToken = access.IsOwner ? conversation.ProtectedShareToken : null
        };
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
            NhAssistantToolPresentationResolver.Deserialize(approval.PresentationJson),
            approval.ArgumentsPreview,
            targets,
            approval.ExpiresAt,
            status);
    }
}
