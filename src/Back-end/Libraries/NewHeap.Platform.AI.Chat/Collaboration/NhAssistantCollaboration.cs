using System.Security.Cryptography;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Collaboration;

/// <summary>
/// Protects invitation-link tokens at rest. The ASP.NET package uses ASP.NET Data Protection.
/// </summary>
internal interface INhAssistantShareTokenProtector
{
    string Protect(string token);

    /// <summary>
    /// True when <paramref name="candidate"/> is the token behind <paramref name="protectedToken"/>.
    /// A token that can no longer be unprotected, for example after a key loss, never matches.
    /// </summary>
    bool Matches(string protectedToken, string candidate);

    /// <summary>
    /// Returns the token, or <see langword="null"/> when it can no longer be unprotected.
    /// </summary>
    string? Unprotect(string protectedToken);
}

/// <summary>
/// Sharing, membership and read state of conversations. Every operation checks ownership or
/// membership itself, records a content-free audit event and announces the change live.
/// </summary>
internal sealed class NhAssistantCollaboration(
    INhAssistantStore store,
    INhAssistantShareTokenProtector tokens,
    NhAssistantLivePublisher live,
    INhAssistantNotifier notifier,
    NhAssistantRegistrationState registration,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks)
{
    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    /// <summary>
    /// Creates a new invitation link; an earlier link stops working. People who already joined stay.
    /// </summary>
    public async Task<TaskResult<string>> CreateShareLinkAsync(
        NhAssistantConversationAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!access.IsOwner)
        {
            return Failed<string>(NhAssistantErrorCodes.OwnerRequired, "Only the owner can share the conversation.");
        }

        var token = NhAssistantBase64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var stored = await store.SetProtectedShareTokenAsync(
            access.Conversation.Id,
            access.ActorId,
            tokens.Protect(token),
            cancellationToken);
        if (!stored)
        {
            return Failed<string>(NhAssistantErrorCodes.ConversationNotFound, "The assistant conversation was not found.");
        }
        await AuditAsync(NhAssistantAuditEventKind.ConversationShareLinkCreated, access, null, cancellationToken);
        return TaskResult<string>.Succeeded(token);
    }

    public async Task<TaskResult> RevokeShareLinkAsync(
        NhAssistantConversationAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!access.IsOwner)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.OwnerRequired, "Only the owner can share the conversation.");
        }
        await store.SetProtectedShareTokenAsync(access.Conversation.Id, access.ActorId, null, cancellationToken);
        await AuditAsync(NhAssistantAuditEventKind.ConversationShareLinkRevoked, access, null, cancellationToken);
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Adds the caller as a participant when the token matches the current invitation link. The
    /// caller must belong to the conversation's tenant; the endpoint has checked the agent policy.
    /// History before joining counts as read.
    /// </summary>
    public async Task<TaskResult<NhAssistantConversationAccess>> JoinAsync(
        Guid conversationId,
        NhAiInvocationContext caller,
        string token,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var existing = await store.FindAccessAsync(conversationId, caller.ActorId, caller.TenantId, cancellationToken);
        if (existing is not null)
        {
            // Opening a link to a conversation one already belongs to just opens it.
            return TaskResult<NhAssistantConversationAccess>.Succeeded(existing);
        }

        var conversation = await FindShareableAsync(conversationId, caller, token, cancellationToken);
        if (conversation is null)
        {
            return Failed<NhAssistantConversationAccess>(
                NhAssistantErrorCodes.ShareLinkInvalid,
                "The invitation link is no longer valid.");
        }

        var latest = (await store.GetConversationStateAsync(conversationId, cancellationToken))?.LastMessageSequence ?? 0;
        var participant = new AssistantConversationParticipant
        {
            ConversationId = conversationId,
            ActorId = caller.ActorId,
            DisplayName = NhAssistantParticipantNames.Normalize(displayName),
            JoinedVia = NhAssistantParticipantSources.Link,
            JoinedAt = DateTimeOffset.UtcNow,
            LastReadSequence = latest
        };
        var added = await store.TryAddParticipantAsync(
            participant,
            registration.Limits.MaxParticipantsPerConversation,
            cancellationToken);
        if (added == NhAssistantParticipantAddResult.LimitReached)
        {
            return Failed<NhAssistantConversationAccess>(
                NhAssistantErrorCodes.ParticipantLimitReached,
                "The conversation has the maximum number of participants.");
        }

        var access = await store.FindAccessAsync(conversationId, caller.ActorId, caller.TenantId, cancellationToken);
        if (access is null)
        {
            return Failed<NhAssistantConversationAccess>(
                NhAssistantErrorCodes.ShareLinkInvalid,
                "The invitation link is no longer valid.");
        }
        if (added == NhAssistantParticipantAddResult.Added)
        {
            await AuditAsync(NhAssistantAuditEventKind.ConversationParticipantJoined, access, caller.ActorId, cancellationToken);
            await MembershipChangedAsync(conversationId, cancellationToken);
        }
        return TaskResult<NhAssistantConversationAccess>.Succeeded(access);
    }

    /// <summary>
    /// Adds a person the application's directory resolved for the owner. The conversation shows as
    /// unread for them and they receive a notification.
    /// </summary>
    public async Task<TaskResult> InviteAsync(
        NhAssistantConversationAccess access,
        NhAssistantDirectoryEntry invitee,
        string ownerDisplayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(invitee);
        if (!access.IsOwner)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.OwnerRequired, "Only the owner can invite people.");
        }
        if (string.Equals(invitee.ActorId, access.ActorId, StringComparison.Ordinal))
        {
            return TaskResult.Succeeded();
        }

        var added = await store.TryAddParticipantAsync(
            new AssistantConversationParticipant
            {
                ConversationId = access.Conversation.Id,
                ActorId = invitee.ActorId,
                DisplayName = NhAssistantParticipantNames.Normalize(invitee.DisplayName),
                JoinedVia = NhAssistantParticipantSources.Invitation,
                InvitedByActorId = access.ActorId,
                JoinedAt = DateTimeOffset.UtcNow,
                LastReadSequence = 0
            },
            registration.Limits.MaxParticipantsPerConversation,
            cancellationToken);
        if (added == NhAssistantParticipantAddResult.LimitReached)
        {
            return TaskResult.Failed(
                NhAssistantErrorCodes.ParticipantLimitReached,
                "The conversation has the maximum number of participants.");
        }
        if (added == NhAssistantParticipantAddResult.Added)
        {
            await AuditAsync(NhAssistantAuditEventKind.ConversationParticipantInvited, access, invitee.ActorId, cancellationToken);
            await MembershipChangedAsync(access.Conversation.Id, cancellationToken);
            await notifier.InvitedAsync(
                access.Conversation.Id,
                invitee.ActorId,
                NhAssistantParticipantNames.Normalize(ownerDisplayName),
                cancellationToken);
        }
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// The owner removes a participant, or a participant leaves. The owner cannot leave; the owner
    /// deletes the conversation instead.
    /// </summary>
    public async Task<TaskResult> RemoveParticipantAsync(
        NhAssistantConversationAccess access,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var leaving = string.Equals(actorId, access.ActorId, StringComparison.Ordinal);
        if (access.IsOwner && leaving)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ParticipantNotFound, "The owner is not a participant.");
        }
        if (!access.IsOwner && !leaving)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.OwnerRequired, "Only the owner can remove participants.");
        }

        var conversationId = access.Conversation.Id;
        if (!await store.TryRemoveParticipantAsync(conversationId, actorId, cancellationToken))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ParticipantNotFound, "The participant was not found.");
        }

        await AuditAsync(
            leaving ? NhAssistantAuditEventKind.ConversationParticipantLeft : NhAssistantAuditEventKind.ConversationParticipantRemoved,
            access,
            actorId,
            cancellationToken);
        await live.PublishToAsync(actorId, new NhAssistantLiveConversationRemoved(conversationId), cancellationToken);
        await MembershipChangedAsync(conversationId, cancellationToken);
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// The owner deletes the conversation for everyone; a participant leaves it.
    /// </summary>
    public async Task<TaskResult> DeleteAsync(
        NhAssistantConversationAccess access,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!access.IsOwner)
        {
            return await RemoveParticipantAsync(access, access.ActorId, cancellationToken);
        }

        var conversationId = access.Conversation.Id;
        var audience = await store.GetAudienceAsync(conversationId, cancellationToken);
        if (!await store.TryArchiveConversationAsync(conversationId, access.ActorId, cancellationToken))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ConversationBusy, "The conversation has an active turn.");
        }

        live.MembershipChanged(conversationId);
        foreach (var actorId in audience)
        {
            await live.PublishToAsync(actorId, new NhAssistantLiveConversationRemoved(conversationId), cancellationToken);
        }
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Raises the caller's read position and tells the caller's other tabs and devices.
    /// </summary>
    public async Task<int?> MarkReadAsync(
        NhAssistantConversationAccess access,
        int sequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var position = await store.MarkReadAsync(access, sequence, cancellationToken);
        if (position is { } read)
        {
            await live.PublishToAsync(
                access.ActorId,
                new NhAssistantLiveConversationRead(access.Conversation.Id, read),
                cancellationToken);
        }
        return position;
    }

    /// <summary>
    /// Stores the caller's current display name when it changed, so others see the right name.
    /// </summary>
    public async Task RefreshDisplayNameAsync(
        NhAssistantConversationAccess access,
        string? displayName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var normalized = NhAssistantParticipantNames.Normalize(displayName);
        var current = access.IsOwner ? access.Conversation.OwnerDisplayName : access.Participant!.DisplayName;
        if (normalized.Length == 0 || string.Equals(current, normalized, StringComparison.Ordinal))
        {
            return;
        }
        await store.UpdateDisplayNameAsync(access, normalized, cancellationToken);
    }

    /// <summary>
    /// The invitation token of the owner's conversation, or <see langword="null"/> without an active link.
    /// </summary>
    public string? GetShareToken(string? protectedToken)
    {
        return protectedToken is null ? null : tokens.Unprotect(protectedToken);
    }

    private async Task<AssistantConversation?> FindShareableAsync(
        Guid conversationId,
        NhAiInvocationContext caller,
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return null;
        }
        var conversation = await store.FindShareableConversationAsync(conversationId, cancellationToken);
        if (conversation?.ProtectedShareToken is null
            || !string.Equals(conversation.TenantId, caller.TenantId, StringComparison.Ordinal)
            || !tokens.Matches(conversation.ProtectedShareToken, token))
        {
            return null;
        }
        return conversation;
    }

    private async Task MembershipChangedAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        live.MembershipChanged(conversationId);
        await live.PublishChangedAsync(conversationId, cancellationToken);
    }

    private async Task AuditAsync(
        NhAssistantAuditEventKind kind,
        NhAssistantConversationAccess access,
        string? objectId,
        CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(
            kind,
            access.Conversation.Id,
            Guid.Empty,
            access.ActorId,
            access.Conversation.AgentId,
            access.Conversation.AgentVersion,
            DateTimeOffset.UtcNow)
        {
            ObjectId = objectId
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private static TaskResult<T> Failed<T>(string code, string message)
    {
        return TaskResult<T>.Failed(code, message);
    }
}
