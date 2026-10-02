using System.Text;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Returns a bounded snapshot of the conversation for the governed tool call that is running now,
/// for example to send along with a reviewed MCP call. It only answers inside an assistant tool
/// call and verifies the invocation, the accountable actor's access and the active turn itself.
/// </summary>
public interface INhAssistantConversationSnapshotProvider
{
    Task<TaskResult<NhAssistantConversationSnapshot>> GetSnapshotAsync(
        NhAiInvocationContext invocation,
        NhAssistantConversationSnapshotRequest? request = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounds of a conversation snapshot. Values outside the hard limits are refused.
/// </summary>
public sealed record NhAssistantConversationSnapshotRequest
{
    public const int MaxRecentMessagesLimit = 20;
    public const int MaxBytesLimit = 65_536;
    public const int MaxMessageBytesLimit = 16_384;

    /// <summary>
    /// Recent messages besides the first question.
    /// </summary>
    public int MaxRecentMessages { get; init; } = 6;

    /// <summary>
    /// UTF-8 bytes of message text in the whole snapshot, the first question included.
    /// </summary>
    public int MaxBytes { get; init; } = 8_192;

    /// <summary>
    /// UTF-8 bytes of one message; longer text is cut and reported as omitted bytes.
    /// </summary>
    public int MaxMessageBytes { get; init; } = 2_048;

    public void Validate()
    {
        if (MaxRecentMessages is < 0 or > MaxRecentMessagesLimit
            || MaxBytes is < 256 or > MaxBytesLimit
            || MaxMessageBytes is < 64 or > MaxMessageBytesLimit
            || MaxMessageBytes > MaxBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NhAssistantConversationSnapshotRequest),
                "The conversation snapshot bounds are outside the supported limits.");
        }
    }
}

/// <summary>
/// The first question and the most recent completed, user-visible text of a conversation. It never
/// contains instructions, tool calls or results, approvals, page context or output of the running turn.
/// </summary>
public sealed record NhAssistantConversationSnapshot(
    Guid ConversationId,
    Guid TurnId,
    string ConversationOwnerActorId,
    string AccountableActorId,
    NhAssistantSnapshotMessage? FirstQuestion,
    IReadOnlyList<NhAssistantSnapshotMessage> RecentMessages,
    int OmittedMessages,
    long OmittedBytes);

/// <summary>
/// One message of a snapshot. <see cref="AuthorActorId"/> names the person who wrote a user message.
/// </summary>
public sealed record NhAssistantSnapshotMessage(
    int Sequence,
    string Role,
    string Text,
    string? AuthorActorId,
    bool Truncated);

/// <summary>
/// Stable, content-free failure codes of <see cref="INhAssistantConversationSnapshotProvider"/>.
/// </summary>
public static class NhAssistantSnapshotCodes
{
    /// <summary>
    /// No assistant tool call runs in the calling flow.
    /// </summary>
    public const string Unavailable = "assistant-snapshot-unavailable";

    /// <summary>
    /// The invocation does not belong to the running call, or its actor lost access.
    /// </summary>
    public const string Forbidden = "assistant-snapshot-forbidden";

    /// <summary>
    /// The conversation no longer runs this turn.
    /// </summary>
    public const string Stale = "assistant-snapshot-stale";
}

internal sealed class NhAssistantConversationSnapshotProvider(INhAssistantStore store)
    : INhAssistantConversationSnapshotProvider
{
    public async Task<TaskResult<NhAssistantConversationSnapshot>> GetSnapshotAsync(
        NhAiInvocationContext invocation,
        NhAssistantConversationSnapshotRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        request ??= new NhAssistantConversationSnapshotRequest();
        request.Validate();

        var turn = NhAssistantExecutionScope.CurrentTurn;
        var call = NhAssistantExecutionScope.CurrentCall;
        if (turn is null || call is null)
        {
            return Failed(NhAssistantSnapshotCodes.Unavailable, "No assistant tool call is running.");
        }
        // The invocation must be the bound context of exactly this call: the agent acting for the
        // accountable person in this turn.
        if (invocation.InvocationId != call.InvocationId
            || invocation.ActorKind != NhAiActorKind.Agent
            || !string.Equals(invocation.ActorId, turn.Agent.ActorId, StringComparison.Ordinal)
            || !string.Equals(invocation.AccountableOwnerId, turn.AccountableOwnerId, StringComparison.Ordinal)
            || !string.Equals(invocation.RunId, turn.TurnId.ToString(), StringComparison.Ordinal))
        {
            return Failed(NhAssistantSnapshotCodes.Forbidden, "The invocation does not belong to the running assistant tool call.");
        }

        var access = await store.FindAccessAsync(turn.ConversationId, turn.OwnerActorId, turn.TenantId, cancellationToken);
        if (access is null)
        {
            return Failed(NhAssistantSnapshotCodes.Forbidden, "The accountable actor has no access to the conversation.");
        }
        if (access.Conversation.ActiveTurnId != turn.TurnId
            || !string.Equals(access.ActiveActorId, turn.OwnerActorId, StringComparison.Ordinal))
        {
            return Failed(NhAssistantSnapshotCodes.Stale, "The conversation no longer runs this turn.");
        }

        var history = await store.GetMessagesAsync(turn.ConversationId, turn.Limits.MaxHistoryMessages, cancellationToken);
        var first = await store.GetFirstUserMessageAsync(turn.ConversationId, cancellationToken);
        // User messages from before conversations could be shared belong to the owner.
        var owner = access.Conversation.OwnerActorId;
        var builder = new SnapshotBuilder(request);
        var firstQuestion = first is not null && Candidate(first, turn.TurnId, owner) is { } firstCandidate
            ? builder.TryAdd(firstCandidate, required: true)
            : null;
        var recent = new List<NhAssistantSnapshotMessage>();
        foreach (var message in history.Reverse())
        {
            if ((first is not null && message.Id == first.Id) || Candidate(message, turn.TurnId, owner) is not { } candidate)
            {
                continue;
            }
            if (recent.Count < request.MaxRecentMessages && builder.TryAdd(candidate, required: false) is { } added)
            {
                recent.Add(added);
            }
            else
            {
                builder.Omit(candidate);
            }
        }
        recent.Reverse();

        return TaskResult<NhAssistantConversationSnapshot>.Succeeded(new NhAssistantConversationSnapshot(
            turn.ConversationId,
            turn.TurnId,
            access.Conversation.OwnerActorId,
            turn.OwnerActorId,
            firstQuestion,
            recent,
            builder.OmittedMessages,
            builder.OmittedBytes));
    }

    /// <summary>
    /// The user-visible text of a completed user or assistant message. Assistant output of the
    /// running turn, including text written before an approval pause, is still incomplete.
    /// </summary>
    private static SnapshotCandidate? Candidate(AssistantMessage message, Guid turnId, string ownerActorId)
    {
        var isUser = message.Role == NhAssistantMessageRoles.User;
        if (!isUser && (message.Role != NhAssistantMessageRoles.Assistant || message.TurnId == turnId))
        {
            return null;
        }
        var text = NhAssistantContent.TextOf(NhAssistantContent.DeserializeParts(message.PartsJson));
        return string.IsNullOrWhiteSpace(text)
            ? null
            : new SnapshotCandidate(message.Sequence, message.Role, text, isUser ? message.AuthorActorId ?? ownerActorId : null);
    }

    private static TaskResult<NhAssistantConversationSnapshot> Failed(string code, string message)
    {
        return TaskResult<NhAssistantConversationSnapshot>.Failed(code, message);
    }

    private sealed record SnapshotCandidate(int Sequence, string Role, string Text, string? AuthorActorId);

    private sealed class SnapshotBuilder(NhAssistantConversationSnapshotRequest request)
    {
        private int _usedBytes;

        public int OmittedMessages { get; private set; }

        public long OmittedBytes { get; private set; }

        public NhAssistantSnapshotMessage? TryAdd(SnapshotCandidate candidate, bool required)
        {
            var size = Encoding.UTF8.GetByteCount(candidate.Text);
            var room = Math.Min(request.MaxMessageBytes, request.MaxBytes - _usedBytes);
            // An optional message is cut only for the per-message limit, never to squeeze it in.
            if (room <= 0 || (!required && Math.Min(size, request.MaxMessageBytes) > room))
            {
                Omit(candidate);
                return null;
            }
            var text = Truncate(candidate.Text, room);
            var kept = Encoding.UTF8.GetByteCount(text);
            _usedBytes += kept;
            OmittedBytes += size - kept;
            return new NhAssistantSnapshotMessage(candidate.Sequence, candidate.Role, text, candidate.AuthorActorId, kept < size);
        }

        public void Omit(SnapshotCandidate candidate)
        {
            OmittedMessages++;
            OmittedBytes += Encoding.UTF8.GetByteCount(candidate.Text);
        }

        private static string Truncate(string text, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            {
                return text;
            }
            var bytes = 0;
            var length = 0;
            while (length < text.Length)
            {
                var width = char.IsHighSurrogate(text[length]) && length + 1 < text.Length ? 2 : 1;
                var next = Encoding.UTF8.GetByteCount(text.AsSpan(length, width));
                if (bytes + next > maxBytes)
                {
                    break;
                }
                bytes += next;
                length += width;
            }
            return text[..length];
        }
    }
}
