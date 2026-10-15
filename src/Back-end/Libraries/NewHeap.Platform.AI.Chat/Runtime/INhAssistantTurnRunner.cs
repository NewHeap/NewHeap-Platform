using System.Collections.Concurrent;
using System.Threading.Channels;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
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
    /// Only the owner or the participant who started the turn may do so.
    /// </summary>
    Task<TaskResult> CancelAsync(
        NhAssistantConversationAccess access,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one headless turn of an agent in its thread, for a background run. The queued inputs
    /// become the turn's messages. Returns <see cref="NhAssistantAgentTurnResult.Busy"/> when another
    /// turn owns the thread.
    /// </summary>
    Task<NhAssistantAgentTurnResult> RunAgentTurnAsync(
        NhAssistantAgentTurnRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Continues a headless turn that waited for an inline approval, after the decision was stored.
    /// </summary>
    Task<NhAssistantAgentTurnResult> ResumeAgentTurnAsync(
        NhAssistantAgentResumeRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes the exact stored invocation of an approved deferred proposal as the agent.
    /// </summary>
    Task<NhAssistantProposalExecutionResult> ExecuteProposalAsync(
        NhAssistantProposalExecutionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one bounded turn of a consulted agent beside its thread: it reads the thread's working
    /// memory, run summaries and recent history, uses the asker's rights only, records changes as
    /// deferred proposals and writes no messages. It never claims the thread.
    /// </summary>
    Task<Runs.NhAssistantConsultationTurnResult> RunConsultationTurnAsync(
        Runs.NhAssistantConsultationTurnRequest request,
        CancellationToken cancellationToken);
}

/// <param name="ConversationId">Conversation that receives the message.</param>
/// <param name="CallerContext">Authenticated invocation context of the human caller.</param>
/// <param name="Text">User message text.</param>
/// <param name="ClientMessageId">Client-generated id that makes a retried POST detectable.</param>
/// <param name="RequestAborted">Token that fires when the client disconnects.</param>
/// <param name="ClientContext">Validated page context of the client, or <see langword="null"/>.</param>
internal sealed record NhAssistantMessageTurnRequest(
    Guid ConversationId,
    NhAiInvocationContext CallerContext,
    string Text,
    string? ClientMessageId,
    CancellationToken RequestAborted,
    string Language = "en",
    NhAssistantClientContext? ClientContext = null)
{
    /// <summary>
    /// The agent side of a turn in an agent thread; <see langword="null"/> in a chat.
    /// </summary>
    public NhAssistantAgentTurnContext? AgentTurn { get; init; }
}

internal sealed record NhAssistantDecisionTurnRequest(
    Guid ConversationId,
    NhAiInvocationContext CallerContext,
    Guid ApprovalId,
    bool Approve,
    string ExpectedProposalHash,
    string? Reason,
    CancellationToken RequestAborted,
    string Language = "en")
{
    public NhAssistantAgentTurnContext? AgentTurn { get; init; }

    /// <summary>
    /// The caller already satisfied the proposal's approval policy, so the decision is not limited
    /// to the person who started the turn.
    /// </summary>
    public bool PolicyAuthorized { get; init; }
}

/// <summary>
/// The agent side of a turn in an agent thread: the subject, the run configuration and who the
/// turn acts for.
/// </summary>
internal sealed record NhAssistantAgentTurnContext(
    AssistantThread Thread,
    NhAiSubjectInfo Subject,
    NhAssistantAgentRunOptions Options,
    Guid? RunId,
    string TriggerId,
    string? InitiatorActorId,
    string AccountableOwnerId,
    bool IsRequestTurn)
{
    public int? Attempt { get; init; }

    public string? FencingToken { get; init; }

    /// <summary>
    /// The turn uses its initiator's rights only, never the agent's grants. Set when an approved
    /// proposal of a consultation executes.
    /// </summary>
    public bool DelegatedOnly { get; init; }

    /// <summary>
    /// The input and output tokens the turn may use before its run stops, from the agent's
    /// <c>MaxTokensPerRun</c> less what earlier turns of the run used; <see langword="null"/>
    /// without a ceiling.
    /// </summary>
    public long? TokenAllowance { get; init; }
}

/// <summary>
/// A message or event the turn receives.
/// </summary>
internal sealed record NhAssistantAgentTurnInput(
    Guid Id,
    string? AuthorActorId,
    string? TriggerId,
    string Text)
{
    public string? PayloadJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record NhAssistantAgentTurnRequest(
    NhAssistantAgentTurnContext AgentTurn,
    IReadOnlyList<NhAssistantAgentTurnInput> Inputs,
    string Language = "en");

internal sealed record NhAssistantAgentResumeRequest(
    NhAssistantAgentTurnContext AgentTurn,
    Guid ApprovalId,
    string Language = "en");

/// <param name="Outcome">One of <see cref="NhAssistantTurnStatuses"/>, or <see cref="Busy"/>.</param>
internal sealed record NhAssistantAgentTurnResult(
    string Outcome,
    string? ErrorCode,
    Guid? TurnId,
    Guid? PendingApprovalId,
    int ToolCalls,
    int InputTokens,
    int OutputTokens,
    int ProposalCount)
{
    /// <summary>
    /// How the turn worked: one of the values in <see cref="NhAssistantRunKinds"/>.
    /// </summary>
    public string Kind { get; init; } = NhAssistantRunKinds.Model;

    public const string Busy = "busy";

    public static NhAssistantAgentTurnResult BusyResult { get; } = new(Busy, NhAssistantErrorCodes.ConversationBusy, null, null, 0, 0, 0, 0);

    public static NhAssistantAgentTurnResult Failed(string code)
    {
        return new NhAssistantAgentTurnResult(NhAssistantTurnStatuses.Failed, code, null, null, 0, 0, 0, 0);
    }
}

internal sealed record NhAssistantProposalExecutionRequest(
    NhAssistantAgentTurnContext AgentTurn,
    Guid ApprovalId);

/// <param name="Threw">The tool threw, so the outcome of a side effect is unknown.</param>
internal sealed record NhAssistantProposalExecutionResult(bool Succeeded, string? ResultCode, bool Threw = false);

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
