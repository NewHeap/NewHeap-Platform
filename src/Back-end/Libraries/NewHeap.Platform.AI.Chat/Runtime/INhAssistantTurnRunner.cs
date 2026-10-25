using System.Collections.Concurrent;
using System.Threading.Channels;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Runs assistant turns inside the HTTP request that started them, so the request-bound
/// invocation context and caller credentials stay available to tool execution. A person's turn that
/// does not depend on its request (one kept after a disconnect, or one the application starts for a
/// person through <c>INhAssistantConversationTurns</c>) carries the person captured when it started,
/// which its tools authorize instead.
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
    /// Validates the answer to the question a person's chat turn waits for, claims the conversation
    /// and resumes the turn with the answer as the result of its <c>ask_question</c> call (LS21).
    /// Only the question's addressee answers; an expired question is refused and releases the
    /// conversation. Expected failures are returned before any event is produced.
    /// </summary>
    Task<TaskResult<NhAssistantTurnHandle>> StartAnswerTurnAsync(
        NhAssistantAnswerTurnRequest request,
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
/// <param name="RequestAborted">Token that cancels the turn when the client disconnects or the caller
/// cancels; <see cref="CancellationToken.None"/> for a turn that outlives its caller
/// (<see cref="NhAssistantOptions.KeepTurnsAfterDisconnect"/>).</param>
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

    /// <summary>
    /// The person captured when the turn started, for a turn that does not depend on the person's
    /// request: the turn makes it ambient, so tools authorize it instead of the request. <see langword="null"/>
    /// for a turn that lives within its request, whose tools authorize the request.
    /// </summary>
    public NhAiPersonInvocation? Person { get; init; }

    /// <summary>
    /// The application's channel that started the turn (<c>INhAssistantConversationTurns</c>), recorded on
    /// the turn's audit events; <see langword="null"/> for the endpoints.
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>
    /// The files the person attached to the message, as the application's file source stored them.
    /// The turn opens them with the person's rights and fails with the code of the first refusal.
    /// </summary>
    public IReadOnlyList<NhAiFileReference> Files { get; init; } = [];
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

    /// <summary>
    /// The person who asked leaves the proposal of a consultation for the subject's approvers instead
    /// of deciding it (CP20); <see cref="Approve"/> is ignored then.
    /// </summary>
    public bool LeaveForApprovers { get; init; }

    /// <inheritdoc cref="NhAssistantMessageTurnRequest.Person"/>
    public NhAiPersonInvocation? Person { get; init; }

    /// <inheritdoc cref="NhAssistantMessageTurnRequest.Channel"/>
    public string? Channel { get; init; }
}

/// <param name="ConversationId">The chat whose turn waits for the question.</param>
/// <param name="CallerContext">Authenticated invocation context of the person who answers.</param>
/// <param name="QuestionId">The question, the id of its <c>ask_question</c> call.</param>
/// <param name="ChoiceIndex">The zero-based index of the chosen answer.</param>
/// <param name="RequestAborted">Token that fires when the client disconnects.</param>
internal sealed record NhAssistantAnswerTurnRequest(
    Guid ConversationId,
    NhAiInvocationContext CallerContext,
    Guid QuestionId,
    int ChoiceIndex,
    CancellationToken RequestAborted,
    string Language = "en")
{
    /// <inheritdoc cref="NhAssistantMessageTurnRequest.Person"/>
    public NhAiPersonInvocation? Person { get; init; }

    /// <inheritdoc cref="NhAssistantMessageTurnRequest.Channel"/>
    public string? Channel { get; init; }
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
    /// The turn uses the agent's own rights only: its grants and, where it acts with them, the rights
    /// of the account it answers to, never its initiator's rights, also for an agent whose runs
    /// delegate. Set when an approved proposal of a consultation that executes with the agent's
    /// rights executes.
    /// </summary>
    public bool AgentRightsOnly { get; init; }

    /// <summary>
    /// The tokens earlier turns of the same execution of the run used; <see langword="null"/> for a
    /// turn outside a run, which has no token ceiling.
    /// </summary>
    public long? RunTokensUsed { get; init; }

    /// <summary>
    /// The skill the run works in at the start of the turn, and who chose it.
    /// </summary>
    public string? SkillId { get; init; }

    public string? SkillChosenBy { get; init; }

    /// <summary>
    /// The people whose messages the run answers: the authors of the messages among the inputs the
    /// run claimed, such as a message typed while the run worked. A turn of the run reads its own
    /// inputs as well; a turn that continues after a wait has only these.
    /// </summary>
    public IReadOnlyList<string> RequestedBy { get; init; } = [];
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

    /// <summary>
    /// The files a person's message carried when it waited for the run (A13). The turn opens them as
    /// attachments, with <see cref="AuthorActorId"/> as actor.
    /// </summary>
    public IReadOnlyList<NhAiFileReference> Files { get; init; } = [];
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

    /// <summary>
    /// The skill the turn ended in; <see langword="null"/> for the agent's own tools.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The turn read results with the agent's own grant that some readers of the thread may not see.
    /// </summary>
    public bool UsedRestrictedResults { get; init; }

    /// <summary>
    /// The answer of a rule turn in each supported UI culture, by culture name, for the run's result
    /// per culture; <see langword="null"/> for a model's answer.
    /// </summary>
    public IReadOnlyDictionary<string, string>? LocalizedAnswer { get; init; }

    /// <summary>
    /// The personal MCP servers the turn left out for want of a person's consent to background use or
    /// connection, or because it acted for someone other than the person who asked; the run records
    /// them and tells the person.
    /// </summary>
    public IReadOnlyList<NhAssistantMcpMissingConnection> MissingMcpConnections { get; init; } = [];

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
