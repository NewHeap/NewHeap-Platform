using System.Collections.Concurrent;
using System.Threading.Channels;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Runs assistant turns inside the HTTP request that started them, so the request-bound
/// invocation context and caller credentials stay available to tool execution.
/// </summary>
internal interface INhAssistantTurnRunner
{
    /// <summary>
    /// Validates and claims the conversation, then starts a turn for a new user message.
    /// Expected failures (unknown conversation, busy conversation, invalid message) are returned
    /// before any event is produced.
    /// </summary>
    Task<TaskResult<NhAssistantTurnHandle>> StartMessageTurnAsync(
        NhAssistantMessageTurnRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates and claims a conversation that waits for approval, then resumes its turn with the
    /// human decision.
    /// </summary>
    Task<TaskResult<NhAssistantTurnHandle>> StartDecisionTurnAsync(
        NhAssistantDecisionTurnRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the running turn of a conversation in this process, or dismisses a pending approval.
    /// </summary>
    Task CancelAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken);
}

/// <param name="ConversationId">Conversation that receives the message.</param>
/// <param name="CallerContext">Authenticated invocation context of the human caller.</param>
/// <param name="Text">User message text.</param>
/// <param name="ClientMessageId">Client-generated id that makes a retried POST detectable.</param>
/// <param name="RequestAborted">Token that fires when the client disconnects.</param>
internal sealed record NhAssistantMessageTurnRequest(
    Guid ConversationId,
    NhAiInvocationContext CallerContext,
    string Text,
    string? ClientMessageId,
    CancellationToken RequestAborted);

internal sealed record NhAssistantDecisionTurnRequest(
    Guid ConversationId,
    NhAiInvocationContext CallerContext,
    Guid ApprovalId,
    bool Approve,
    string ExpectedProposalHash,
    string? Reason,
    CancellationToken RequestAborted);

/// <summary>
/// A started turn: its event stream and its completion.
/// </summary>
internal sealed class NhAssistantTurnHandle(
    ChannelReader<NhAssistantTurnEvent> events,
    Task completion)
{
    public ChannelReader<NhAssistantTurnEvent> Events { get; } = events;

    public Task Completion { get; } = completion;
}

/// <summary>
/// In-process registry of running turns for <c>POST cancel</c>. Cancellation reaches only turns
/// that run in the same process; multi-node cancellation is outside the library scope, and an
/// abandoned running turn is recovered after its deadline.
/// </summary>
internal sealed class NhAssistantTurnCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, Registration> _running = new();

    public CancellationTokenSource Register(
        Guid conversationId,
        Guid turnId,
        CancellationToken requestAborted)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        _running[conversationId] = new Registration(turnId, source);
        return source;
    }

    public bool Cancel(Guid conversationId)
    {
        if (!_running.TryGetValue(conversationId, out var registration))
        {
            return false;
        }
        registration.Cancelled = true;
        try
        {
            registration.Source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished while it was being cancelled.
        }
        return true;
    }

    public bool WasCancelled(Guid conversationId, Guid turnId)
    {
        return _running.TryGetValue(conversationId, out var registration)
            && registration.TurnId == turnId
            && registration.Cancelled;
    }

    public void Unregister(Guid conversationId, Guid turnId)
    {
        if (_running.TryGetValue(conversationId, out var registration) && registration.TurnId == turnId)
        {
            _running.TryRemove(new KeyValuePair<Guid, Registration>(conversationId, registration));
        }
    }

    private sealed class Registration(Guid turnId, CancellationTokenSource source)
    {
        public Guid TurnId { get; } = turnId;

        public CancellationTokenSource Source { get; } = source;

        public volatile bool Cancelled;
    }
}
