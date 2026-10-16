using System.Globalization;
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
                        parts.Add(ToView(invocation, LocalizedToolName(invocation, approvals)));
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
                // An agent message keeps its author only to tell a rule's message from the model's.
                AuthorActorId = message.Role == NhAssistantMessageRoles.User
                    ? message.AuthorActorId ?? conversation.OwnerActorId
                    : message.AuthorActorId
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
            Kind = conversation.Kind,
            LastReadSequence = access.LastReadSequence ?? lastMessageSequence,
            LastMessageSequence = lastMessageSequence,
            ActiveActorId = access.ActiveActorId,
            Members = members,
            ProtectedShareToken = access.IsOwner ? conversation.ProtectedShareToken : null
        };
    }

    public static NhAssistantToolCallPartView ToView(AssistantToolInvocation invocation, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new NhAssistantToolCallPartView(
            invocation.Id,
            invocation.ToolId,
            invocation.ToolVersion,
            displayName ?? invocation.DisplayName,
            invocation.Status,
            NhAssistantContent.Preview(invocation.ArgumentsJson, invocation.DataClassification),
            NhAssistantContent.ResultPreview(invocation.ResultJson, invocation.DataClassification),
            invocation.ResultCode)
        {
            GrantSource = invocation.GrantSource,
            ReadForActorId = invocation.ReadForActorId
        };
    }

    /// <summary>
    /// The tool name of a call that waits for or waited for a proposal, in the viewer's culture; the
    /// stored name of other calls is the one their turn used.
    /// </summary>
    private static string? LocalizedToolName(
        AssistantToolInvocation invocation,
        IReadOnlyDictionary<Guid, AssistantApproval> approvals)
    {
        return invocation.ApprovalId is { } approvalId && approvals.TryGetValue(approvalId, out var approval)
            ? NhAssistantToolPresentationResolver.Pick(
                approval.PresentationJson,
                approval.PresentationsJson,
                CultureInfo.CurrentUICulture)?.ToolDisplayName
            : null;
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
            NhAssistantToolPresentationResolver.Pick(approval.PresentationJson, approval.PresentationsJson, CultureInfo.CurrentUICulture),
            approval.ArgumentsPreview,
            targets,
            approval.ExpiresAt,
            status)
        {
            StaleKind = StaleKindOf(approval),
            StaleExplanation = Runs.NhAssistantStaleExplanations.Pick(approval.StaleExplanationsJson, CultureInfo.CurrentUICulture),
            Rationale = Runs.NhAssistantStaleExplanations.Pick(approval.RationalesJson, CultureInfo.CurrentUICulture),
            SupersededByApprovalId = approval.SupersededByApprovalId
        };
    }

    /// <summary>
    /// The kind of a stale proposal; proposals closed as stale before kinds existed changed.
    /// </summary>
    public static string? StaleKindOf(AssistantApproval approval)
    {
        return approval.Status == NhAssistantApprovalStatuses.Stale
            ? approval.StaleKind ?? NhAssistantStaleKinds.Changed
            : null;
    }
}
