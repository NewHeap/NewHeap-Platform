using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Sends a person's message to their chat, decides the approval their turn waits for and answers the
/// question it asks, for the application's own channels, such as a messaging app in which a person talks
/// with the assistant; <see cref="INhAssistantConversations"/> creates, reads and stops the chat. It
/// is a server API for the application's code; there is no endpoint for it. The application vouches for
/// the person, as for <see cref="INhAssistantProposalDecisionService"/>: it authenticated them through
/// its channel and passes their principal, actor id and tenant, as the assistant's context resolver
/// gives them for the person's own requests.
/// <para>
/// A turn is the turn of <c>POST conversations/{id}/messages</c>,
/// <c>POST conversations/{id}/approvals/{approvalId}/decide</c> and
/// <c>POST conversations/{id}/questions/{questionId}/answer</c>, through the same runner, with the same
/// access rules: the person's conversation in their tenant, the assistant's access policy, the agent's
/// own policy, the decision rules and the person's tools. It runs without a request of the person, in
/// services of its own and without the caller's request: an invocation scope of the person makes their
/// principal and invocation context ambient, so the person's tools authorize that principal, never the
/// caller's request. Tools of the API bridge need the person's own request and are not offered. The
/// turn's tools, policies and turn context providers find the conversation and the channel on the
/// invocation context (<see cref="NhAiInvocationContext.ConversationId"/>,
/// <see cref="NhAiInvocationContext.Channel"/>) and the person through <see cref="INhAiTurnPerson"/>.
/// </para>
/// <para>
/// Only chats are served; an agent's thread answers <c>assistant-conversation-not-found</c>, as an agent
/// kept out of the chat reads as unknown. The audit records the channel on the turn's events.
/// </para>
/// <para>
/// The interface grows with the endpoints, so implement or mock it only in the application's own tests.
/// </para>
/// </summary>
public interface INhAssistantConversationTurns
{
    /// <summary>
    /// Sends <paramref name="message"/> as <paramref name="person"/> and returns the turn's result once it
    /// ended or waits for an approval or a question. A chat whose question or approval passed its lifetime
    /// takes the message: what it waited for closes as expired first, as a late answer or decision closes
    /// it (CS9).
    /// <para>
    /// Fails with the endpoint's codes: <c>assistant-disabled</c>; <c>assistant-context-unavailable</c>
    /// when the application's resolver refuses the principal or resolves another actor or tenant than the
    /// person names; <c>assistant-conversation-not-found</c> for a conversation the person does not take
    /// part in, of another tenant, or an agent's thread; <c>assistant-agent-not-found</c>;
    /// <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy or the agent's
    /// policy; <c>assistant-conversation-busy</c>, <c>assistant-message-invalid</c> (also for a
    /// <see cref="NhAssistantConversationMessage.ClientContext"/> the endpoint would not keep whole),
    /// <c>assistant-message-too-long</c>, <c>assistant-message-duplicate</c> (the same
    /// <see cref="NhAssistantConversationMessage.ClientMessageId"/> again, such as a delivery the channel
    /// repeats) and the attachment codes. A turn that started and then failed is a result with status
    /// <c>failed</c> and its code.
    /// </para>
    /// <para>
    /// When the caller's <paramref name="cancellationToken"/> fires, the call throws
    /// <see cref="OperationCanceledException"/>; the turn is cancelled with it, unless
    /// <see cref="NhAssistantConversationTurnOptions.KeepTurnAfterCallerLeaves"/> lets it continue up to
    /// <see cref="NhAssistantLimits.TurnTimeout"/>, after which the conversation and its live updates
    /// carry the result.
    /// </para>
    /// </summary>
    /// <param name="person">The person the application authenticated.</param>
    /// <param name="conversationId">The person's chat.</param>
    /// <param name="message">The message.</param>
    /// <param name="options">The application's channel and the turn's language, active division and
    /// lifetime.</param>
    Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationMessage message,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides the approval the person's turn in the chat waits for, or the proposal of a consultation the
    /// person decides there as the one who asked, and returns the resumed turn's result.
    /// <para>
    /// Fails with the endpoint's codes, as <see cref="SendAsync"/> does for the person and the
    /// conversation, and for the approval: <c>assistant-approval-not-found</c>,
    /// <c>assistant-approval-not-pending</c>, <c>assistant-approval-forbidden</c> (another participant's
    /// turn), <c>assistant-proposal-hash-mismatch</c>, <c>assistant-approval-policy-denied</c>,
    /// <c>assistant-proposal-stale</c> and <c>assistant-approval-decision-invalid</c> (a reason longer than
    /// 500 characters). An approval that expired is a result with status <c>failed</c> and
    /// <c>assistant-approval-expired</c>. The caller's cancellation works as for <see cref="SendAsync"/>.
    /// </para>
    /// </summary>
    Task<TaskResult<NhAssistantConversationTurnResult>> DecideAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid approvalId,
        NhAssistantConversationDecision decision,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the question the person's chat waits for (LS21), as
    /// <c>POST conversations/{id}/questions/{questionId}/answer</c> does, and returns the resumed turn's
    /// result: the answer is the result of the agent's <c>ask_question</c> call, and the turn continues
    /// with the rights of the person who answered.
    /// <para>
    /// Fails with the codes of <see cref="SendAsync"/> for the person and the conversation, and for the
    /// question in the endpoint's order: <c>assistant-question-not-found</c>,
    /// <c>assistant-question-forbidden</c> (the question asks another person, checked before its status
    /// so nobody else learns it), <c>assistant-question-not-pending</c>,
    /// <c>assistant-question-answer-invalid</c> (a choice outside the question) and
    /// <c>assistant-question-expired</c>, which also closes the question and releases the chat. The
    /// caller's cancellation works as for <see cref="SendAsync"/>.
    /// </para>
    /// </summary>
    /// <param name="questionId">The question, from <see cref="NhAssistantConversationTurnResult.PendingQuestion"/>.</param>
    /// <param name="answer">The chosen answer.</param>
    Task<TaskResult<NhAssistantConversationTurnResult>> AnswerAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid questionId,
        NhAssistantConversationAnswer answer,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the turn of the person's chat, whoever started it and from wherever, and returns its
    /// result once it ended or waits for an approval or a question, so a channel whose caller gave up on
    /// <see cref="SendAsync"/>, such as a tool call with a time limit, can read the answer later without
    /// polling. A chat without a running turn answers at once. The wait reads and changes nothing of the
    /// turn: it neither cancels it nor keeps it alive.
    /// <para>
    /// When <see cref="NhAssistantTurnWait.Timeout"/> passes first, the result has status
    /// <see cref="NhAssistantTurnStatuses.Running"/> and no message. A turn that ended while the wait ran
    /// on this host gives the status, code, usage and timings of its end. A turn that ended earlier, or on
    /// another host, is read from the conversation, which keeps no outcome per turn: its result has
    /// status <c>completed</c>, <c>waiting-for-approval</c> or <c>failed</c>, a cancelled turn reads as
    /// <c>completed</c>, a failed one has no code, and neither has timings.
    /// </para>
    /// <para>
    /// Fails with the codes of <see cref="SendAsync"/> for the person and the conversation, also when the
    /// person loses access to the chat during the wait. When the caller's
    /// <paramref name="cancellationToken"/> fires, the call throws
    /// <see cref="OperationCanceledException"/>; the turn goes on.
    /// </para>
    /// </summary>
    /// <param name="wait">How long to wait, the language of the result and the progress to report.</param>
    Task<TaskResult<NhAssistantConversationTurnResult>> WaitAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantTurnWait wait,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// How <see cref="INhAssistantConversationTurns.WaitAsync"/> waits.
/// </summary>
/// <param name="Timeout">How long to wait at most, from zero, which only reads the turn's state, to
/// <see cref="MaxTimeout"/>.</param>
public sealed record NhAssistantTurnWait(TimeSpan Timeout)
{
    /// <summary>
    /// The longest <see cref="Timeout"/>: the longest a model call may take.
    /// </summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The language of the result: <c>nl</c> for Dutch and <c>en</c> otherwise, as
    /// <see cref="NhAssistantConversationTurnOptions.Language"/>.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// The person's active division, as <see cref="NhAssistantConversationTurnOptions.ActiveDivisionId"/>.
    /// </summary>
    public Guid? ActiveDivisionId { get; init; }

    /// <summary>
    /// Receives what the turn does from here on, while it runs on this host; a turn on another host
    /// reports nothing here, and the wait still sees its end. <see langword="null"/> without one.
    /// </summary>
    public IProgress<NhAssistantTurnProgress>? Progress { get; init; }
}

/// <summary>
/// A person's message through <see cref="INhAssistantConversationTurns.SendAsync"/>.
/// </summary>
/// <param name="Text">The message, at most <see cref="NhAssistantLimits.MaxMessageChars"/> characters.</param>
public sealed record NhAssistantConversationMessage(string Text)
{
    /// <summary>
    /// The channel's id of the message, at most 128 characters, such as the id of its delivery: the same
    /// id again is refused with <c>assistant-message-duplicate</c>, so a delivery the channel repeats
    /// starts no second turn. <see langword="null"/> without one.
    /// </summary>
    public string? ClientMessageId { get; init; }

    /// <summary>
    /// The files the person attached, as the application's file source stored them; the turn opens them
    /// with the person's rights, as for a message of the endpoint.
    /// </summary>
    public IReadOnlyList<NhAiFileReference> Files { get; init; } = [];

    /// <summary>
    /// The page the person has open, such as the page around a website widget (FB52): the
    /// <c>clientContext</c> of <c>POST conversations/{id}/messages</c>, with its route, title and at most
    /// five entities. The turn shows it to the model as the person's screen, untrusted data that grants
    /// nothing, and the message stores it, so a turn that resumes after an approval or a question shows it
    /// again. The endpoint's reading validates it: text is cleaned and cut off at the endpoint's limits, and
    /// the same context gives the same screen and the same stored context. A context the endpoint would
    /// ignore or only partly keep is refused with <c>assistant-message-invalid</c>: a route without text,
    /// more than five entities, an entity that is <see langword="null"/>, whose type is not dash-case of at
    /// most 60 characters, or whose id is empty, longer than 64 characters or holds a control character, and
    /// a context whose stored form exceeds the 4,000 characters the message keeps for it, which the
    /// endpoint ignores: text in any language fits at every limit, but HTML-sensitive characters such as
    /// <c>&amp;</c> or emoji in nearly every field at its limit do not. <see langword="null"/> without one.
    /// </summary>
    public NhAssistantClientContextDto? ClientContext { get; init; }
}

/// <summary>
/// A person's decision through <see cref="INhAssistantConversationTurns.DecideAsync"/>.
/// </summary>
/// <param name="Decision">Approve or reject.</param>
/// <param name="ExpectedProposalHash">The hash of the proposal the person saw, from
/// <see cref="NhAssistantApprovalPartDto.ProposalHash"/> of a turn's result; a proposal that changed
/// since is refused.</param>
public sealed record NhAssistantConversationDecision(
    NhAssistantProposalDecision Decision,
    string ExpectedProposalHash)
{
    /// <summary>
    /// Why the person decided so, at most 500 characters; the model reads it with a rejection.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// A person's answer through <see cref="INhAssistantConversationTurns.AnswerAsync"/>.
/// </summary>
/// <param name="Choice">The zero-based index of the chosen answer among the question's
/// <see cref="NhAssistantQuestionPartDto.Choices"/>.</param>
public sealed record NhAssistantConversationAnswer(int Choice);

/// <summary>
/// How a turn through <see cref="INhAssistantConversationTurns"/> runs.
/// </summary>
/// <param name="Channel">The application's channel, a dash-case code of at most
/// <see cref="NhAssistantDecisionChannels.MaxLength"/> characters such as <c>messaging-chat</c>, which the
/// audit events of the turn record (<see cref="NhAssistantAuditEvent.Channel"/>).</param>
public sealed record NhAssistantConversationTurnOptions(string Channel)
{
    /// <summary>
    /// The language of the turn: <c>nl</c> for Dutch and <c>en</c> otherwise, as the endpoints read
    /// <c>Accept-Language</c>. The turn also runs in the application's supported culture of that language.
    /// </summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// The person's active division, as the active-division header of the person's own request would
    /// name it. It counts only when the host maps the active division (<c>AddActiveDivisionScope</c>) and
    /// the person passes its policy for that division; the turn's context then carries the division and
    /// its capabilities, and division policies read it. <see langword="null"/> without one.
    /// </summary>
    public Guid? ActiveDivisionId { get; init; }

    /// <summary>
    /// Whether the turn keeps running when the caller's cancellation token fires, up to
    /// <see cref="NhAssistantLimits.TurnTimeout"/>: <see langword="true"/> lets it outlive the caller,
    /// <see langword="false"/> cancels it with the caller, and <see langword="null"/> (the default) follows
    /// <see cref="NhAssistantOptions.KeepTurnsAfterDisconnect"/>.
    /// </summary>
    public bool? KeepTurnAfterCallerLeaves { get; init; }

    /// <summary>
    /// Receives what the turn does while it runs, so a channel can show activity instead of silence:
    /// the caller's own thread does not wait for it, and a handler that throws is logged and asked no
    /// further, so it never stops the turn. <see langword="null"/> without one.
    /// </summary>
    public IProgress<NhAssistantTurnProgress>? Progress { get; init; }
}

/// <summary>
/// How a person's turn through <see cref="INhAssistantConversationTurns"/> ended, as the conversation
/// stored it.
/// </summary>
/// <param name="ConversationId">The chat.</param>
/// <param name="TurnId">The turn.</param>
/// <param name="Status">One of <see cref="NhAssistantTurnStatuses"/>: <c>completed</c>,
/// <c>waiting-for-approval</c>, <c>cancelled</c> or <c>failed</c>.</param>
/// <param name="ErrorCode">The code of a failed turn, or of a completed turn that reached its tool-call
/// limit; <see langword="null"/> otherwise.</param>
/// <param name="Message">The assistant's message of the turn, as the conversation shows it to the
/// person; <see langword="null"/> when the turn stored none.</param>
/// <param name="PendingApprovals">The approvals the conversation waits for after the turn, which
/// <see cref="INhAssistantConversationTurns.DecideAsync"/> decides; empty when it waits for none.</param>
/// <param name="Usage">The model tokens and tool calls of the turn.</param>
public sealed record NhAssistantConversationTurnResult(
    Guid ConversationId,
    Guid TurnId,
    string Status,
    string? ErrorCode,
    NhAssistantMessageDto? Message,
    IReadOnlyList<NhAssistantApprovalPartDto> PendingApprovals,
    NhAssistantUsageDto Usage)
{
    /// <summary>
    /// The question the conversation waits for after the turn (LS21), which
    /// <see cref="INhAssistantConversationTurns.AnswerAsync"/> answers when it asks the person; the
    /// status is then <c>waiting-for-approval</c>. <see langword="null"/> when it waits for none.
    /// </summary>
    public NhAssistantQuestionPartDto? PendingQuestion { get; init; }

    /// <summary>
    /// Where the time of the turn went; <see langword="null"/> for a turn that ended before its model or
    /// that <see cref="INhAssistantConversationTurns.WaitAsync"/> read from the conversation.
    /// </summary>
    public NhAssistantTurnTimings? Timings { get; init; }
}

/// <summary>
/// Runs a person's turn without the caller's request, in services of its own that live as long as the
/// turn, so the turn can outlive the caller (LS19).
/// </summary>
internal sealed class NhAssistantConversationTurns(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<NhAssistantOptions> options,
    ILogger<NhAssistantConversationTurns> logger) : INhAssistantConversationTurns
{
    public Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationMessage message,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(message);
        ValidateOptions(options);
        var clientContextValid = TryReadClientContext(message.ClientContext, out var clientContext);

        return RunAsync(
            person,
            conversationId,
            options,
            async (turn, token) =>
            {
                // Refused with the message's other checks, after the person and the chat.
                if (!clientContextValid)
                {
                    return TaskResult<NhAssistantTurnHandle>.Failed(
                        NhAssistantErrorCodes.MessageInvalid,
                        "The page the person has open is invalid.");
                }

                var services = turn.Services;
                // Other people in a shared conversation see the sender's current name.
                await services.GetRequiredService<NhAssistantCollaboration>().RefreshDisplayNameAsync(
                    turn.Access,
                    await services.GetRequiredService<INhAssistantDisplayNameResolver>()
                        .GetDisplayNameAsync(person.User, turn.Person.Context, token),
                    token);

                return await services.GetRequiredService<INhAssistantTurnRunner>().StartMessageTurnAsync(
                    new NhAssistantMessageTurnRequest(
                        conversationId,
                        turn.Person.Context,
                        message.Text ?? string.Empty,
                        message.ClientMessageId,
                        turn.TurnCancellation,
                        turn.Language,
                        clientContext)
                    {
                        Files = message.Files ?? [],
                        Person = turn.Person,
                        Channel = options.Channel
                    },
                    token);
            },
            cancellationToken);
    }

    public Task<TaskResult<NhAssistantConversationTurnResult>> DecideAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid approvalId,
        NhAssistantConversationDecision decision,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.ExpectedProposalHash, nameof(decision));
        if (!Enum.IsDefined(decision.Decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision.Decision, "A decision approves or rejects.");
        }
        ValidateOptions(options);

        return RunAsync(
            person,
            conversationId,
            options,
            async (turn, token) =>
            {
                var approve = decision.Decision == NhAssistantProposalDecision.Approve;
                // A consultation's proposal waits in the consulted agent's thread; its asker decides it
                // here with their own rights, checked as any approver is.
                var asker = await NhAssistantAskerDecisions.FindAsync(
                    turn.Services,
                    conversationId,
                    approvalId,
                    turn.Person.Context.ActorId,
                    person.User,
                    approve,
                    token);
                if (asker.ErrorCode is { } code)
                {
                    return TaskResult<NhAssistantTurnHandle>.Failed(code, "The approval cannot be decided by this person.");
                }

                return await turn.Services.GetRequiredService<INhAssistantTurnRunner>().StartDecisionTurnAsync(
                    new NhAssistantDecisionTurnRequest(
                        conversationId,
                        turn.Person.Context,
                        approvalId,
                        approve,
                        decision.ExpectedProposalHash,
                        string.IsNullOrWhiteSpace(decision.Reason) ? null : decision.Reason.Trim(),
                        turn.TurnCancellation,
                        turn.Language)
                    {
                        Person = turn.Person,
                        Channel = options.Channel
                    },
                    token);
            },
            cancellationToken);
    }

    public Task<TaskResult<NhAssistantConversationTurnResult>> AnswerAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid questionId,
        NhAssistantConversationAnswer answer,
        NhAssistantConversationTurnOptions options,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(answer);
        ValidateOptions(options);

        return RunAsync(
            person,
            conversationId,
            options,
            // The runner checks the question in the endpoint's order (CS10) and claims the chat for the
            // person who answers.
            (turn, token) => turn.Services.GetRequiredService<INhAssistantTurnRunner>().StartAnswerTurnAsync(
                new NhAssistantAnswerTurnRequest(
                    conversationId,
                    turn.Person.Context,
                    questionId,
                    answer.Choice,
                    turn.TurnCancellation,
                    turn.Language)
                {
                    Person = turn.Person,
                    Channel = options.Channel
                },
                token),
            cancellationToken);
    }

    /// <summary>
    /// Runs the turn in a flow of its own: without the caller's request, so nothing in the turn reads
    /// the caller's request or its principal, and in services of its own that end with the turn. The
    /// caller waits for the result until its token fires.
    /// </summary>
    private async Task<TaskResult<NhAssistantConversationTurnResult>> RunAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationTurnOptions turnOptions,
        Func<PreparedTurn, CancellationToken, Task<TaskResult<NhAssistantTurnHandle>>> start,
        CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (!current.Enabled)
        {
            return TaskResult<NhAssistantConversationTurnResult>.Failed(
                NhAssistantErrorCodes.Disabled,
                "The assistant is disabled.");
        }

        var keep = turnOptions.KeepTurnAfterCallerLeaves ?? current.KeepTurnsAfterDisconnect;
        return await NhAssistantPersonCalls.RunWithoutCallerAsync(
            scopeFactory,
            turnOptions.Language,
            (services, language) => RunInScopeAsync(services, language, person, conversationId, turnOptions, keep, start, cancellationToken),
            cancellationToken);
    }

    private async Task<TaskResult<NhAssistantConversationTurnResult>> RunInScopeAsync(
        IServiceProvider services,
        string language,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationTurnOptions turnOptions,
        bool keep,
        Func<PreparedTurn, CancellationToken, Task<TaskResult<NhAssistantTurnHandle>>> start,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(services, person, conversationId, turnOptions.ActiveDivisionId, cancellationToken);
        if (!prepared.Success)
        {
            return TaskResult<NhAssistantConversationTurnResult>.Failed(prepared);
        }

        var (personInvocation, access) = prepared.Data;
        var started = await start(
            new PreparedTurn(services, personInvocation, access, language, keep ? CancellationToken.None : cancellationToken),
            cancellationToken);
        if (!started.Success)
        {
            return TaskResult<NhAssistantConversationTurnResult>.Failed(started);
        }

        try
        {
            return TaskResult<NhAssistantConversationTurnResult>.Succeeded(
                await CollectAsync(services, personInvocation.Context, conversationId, started.Data, turnOptions.Progress));
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is not OperationCanceledException)
        {
            // Nobody waits for the turn any more; its failure would go unnoticed otherwise.
            logger.LogWarning(
                "Assistant turn of conversation {ConversationId} through channel {Channel} failed after its caller left ({ExceptionType}).",
                conversationId,
                turnOptions.Channel,
                exception.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Resolves the person and checks the endpoints' access rules before the turn claims the conversation.
    /// </summary>
    private static async Task<TaskResult<(NhAiPersonInvocation Person, NhAssistantConversationAccess Access)>> PrepareAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        Guid? activeDivisionId,
        CancellationToken cancellationToken)
    {
        var resolved = await NhAssistantPersonCalls.ResolveAsync(services, person, activeDivisionId, cancellationToken);
        if (!resolved.Success)
        {
            return TaskResult<(NhAiPersonInvocation, NhAssistantConversationAccess)>.Failed(resolved);
        }

        var personInvocation = resolved.Data;
        var chat = await NhAssistantPersonCalls.FindChatAsync(services, personInvocation.Context, conversationId, cancellationToken);
        if (!chat.Success)
        {
            return TaskResult<(NhAiPersonInvocation, NhAssistantConversationAccess)>.Failed(chat);
        }

        var access = chat.Data;
        var effective = await services.GetRequiredService<NhAssistantAgentCatalog>()
            .FindAsync(access.Conversation.AgentId, includeDisabled: false, cancellationToken);
        if (effective is null)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
        }

        // Every endpoint requires the assistant's access policy, and a turn the agent's own policy.
        if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(services, person, personInvocation)
            || !await services.GetRequiredService<NhAssistantAgentAccess>().CanUseAsync(person.User, effective.Definition))
        {
            return Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant agent.");
        }

        return TaskResult<(NhAiPersonInvocation, NhAssistantConversationAccess)>.Succeeded((personInvocation, access));
    }

    /// <summary>
    /// Reads the turn's events until it ends, as the stream of the endpoint would carry them, and returns
    /// what the conversation stored.
    /// </summary>
    private async Task<NhAssistantConversationTurnResult> CollectAsync(
        IServiceProvider services,
        NhAiInvocationContext caller,
        Guid conversationId,
        NhAssistantTurnHandle handle,
        IProgress<NhAssistantTurnProgress>? progress)
    {
        var reporter = new NhAssistantTurnProgressReporter(progress, logger);
        var turnId = Guid.Empty;
        Guid? messageId = null;
        var status = NhAssistantTurnStatuses.Failed;
        string? errorCode = null;
        var usage = new NhAssistantTurnUsage(0, 0, 0);
        NhAssistantTurnTimings? timings = null;
        await foreach (var evt in handle.Events.ReadAllAsync(CancellationToken.None))
        {
            switch (evt)
            {
                case NhAssistantTurnStartedEvent turnStarted:
                    turnId = turnStarted.TurnId;
                    messageId = turnStarted.AssistantMessageId;
                    break;
                case NhAssistantTurnCompletedEvent completed:
                    status = completed.Status;
                    errorCode = completed.ErrorCode;
                    usage = completed.Usage;
                    timings = completed.Timings;
                    break;
                case NhAssistantErrorEvent failed:
                    status = NhAssistantTurnStatuses.Failed;
                    errorCode = failed.Code;
                    break;
            }
            reporter.Report(evt, turnId);
        }
        await handle.Completion;

        return await ReadResultAsync(services, caller, conversationId, turnId, messageId, status, errorCode, usage, timings);
    }

    /// <summary>
    /// The result of a turn with the conversation as it stands after it, as the person reads it.
    /// </summary>
    private static async Task<NhAssistantConversationTurnResult> ReadResultAsync(
        IServiceProvider services,
        NhAiInvocationContext caller,
        Guid conversationId,
        Guid turnId,
        Guid? messageId,
        string status,
        string? errorCode,
        NhAssistantTurnUsage usage,
        NhAssistantTurnTimings? timings)
    {
        var access = await services.GetRequiredService<INhAssistantStore>()
            .FindAccessAsync(conversationId, caller.ActorId, caller.TenantId, CancellationToken.None);
        NhAssistantMessageDto? message = null;
        NhAssistantApprovalPartDto[] pending = [];
        NhAssistantQuestionPartDto? question = null;
        if (access is not null)
        {
            var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(access, CancellationToken.None);
            var conversation = NhAssistantDtoMapper.ToDto(view, caller.ActorId);
            message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            pending = conversation.PendingApproval is NhAssistantApprovalPartDto approval ? [approval] : [];
            question = conversation.PendingQuestion as NhAssistantQuestionPartDto;
        }

        return new NhAssistantConversationTurnResult(
            conversationId,
            turnId,
            status,
            errorCode,
            message,
            pending,
            new NhAssistantUsageDto(usage.InputTokens, usage.OutputTokens, usage.ToolCalls))
        {
            PendingQuestion = question,
            Timings = timings
        };
    }

    public Task<TaskResult<NhAssistantConversationTurnResult>> WaitAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantTurnWait wait,
        CancellationToken cancellationToken = default)
    {
        NhAssistantPersonCalls.ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(wait);
        if (wait.Timeout < TimeSpan.Zero || wait.Timeout > NhAssistantTurnWait.MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(wait), wait.Timeout, "A wait lasts from zero to 15 minutes.");
        }

        return RunWaitAsync(person, conversationId, wait, cancellationToken);
    }

    private async Task<TaskResult<NhAssistantConversationTurnResult>> RunWaitAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantTurnWait wait,
        CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.Enabled)
        {
            return TaskResult<NhAssistantConversationTurnResult>.Failed(
                NhAssistantErrorCodes.Disabled,
                "The assistant is disabled.");
        }

        return await NhAssistantPersonCalls.RunWithoutCallerAsync(
            scopeFactory,
            wait.Language,
            (services, _) => WaitInScopeAsync(services, person, conversationId, wait, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Follows the chat until its turn stopped running. The turn's events bring its end at once on this
    /// host; the conversation is read as well, at an interval, for a turn on another host and for access
    /// that ended. The watch starts before the first read, so a turn that ends in between is not missed.
    /// </summary>
    private async Task<TaskResult<NhAssistantConversationTurnResult>> WaitInScopeAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantTurnWait wait,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(services, person, conversationId, wait.ActiveDivisionId, cancellationToken);
        if (!prepared.Success)
        {
            return TaskResult<NhAssistantConversationTurnResult>.Failed(prepared);
        }

        var caller = prepared.Data.Person.Context;
        var store = services.GetRequiredService<INhAssistantStore>();
        var timing = services.GetService<NhAssistantTiming>() ?? NhAssistantTiming.Default;
        var reporter = new NhAssistantTurnProgressReporter(wait.Progress, logger);
        using var watch = services.GetRequiredService<NhAssistantTurnWatchers>().Watch(conversationId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(wait.Timeout);
        while (true)
        {
            var access = await store.FindAccessAsync(conversationId, caller.ActorId, caller.TenantId, cancellationToken);
            if (access is null)
            {
                return TaskResult<NhAssistantConversationTurnResult>.Failed(
                    NhAssistantErrorCodes.ConversationNotFound,
                    "The assistant conversation was not found.");
            }

            var conversation = access.Conversation;
            if (conversation.Status != NhAssistantConversationStatuses.Running)
            {
                return TaskResult<NhAssistantConversationTurnResult>.Succeeded(
                    await ReadEndedTurnAsync(services, caller, conversation));
            }

            var turnId = conversation.ActiveTurnId ?? Guid.Empty;
            if (deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return TaskResult<NhAssistantConversationTurnResult>.Succeeded(
                    new NhAssistantConversationTurnResult(
                        conversationId,
                        turnId,
                        NhAssistantTurnStatuses.Running,
                        null,
                        null,
                        [],
                        new NhAssistantUsageDto(0, 0, 0)));
            }

            var end = await FollowAsync(watch, turnId, reporter, timing.TurnWaitCheckInterval, deadline.Token);
            if (end is not null)
            {
                return TaskResult<NhAssistantConversationTurnResult>.Succeeded(
                    await ReadResultAsync(
                        services,
                        caller,
                        conversationId,
                        end.TurnId == Guid.Empty ? turnId : end.TurnId,
                        await FindAnswerAsync(store, conversationId, turnId),
                        end.Status,
                        end.ErrorCode,
                        end.Usage,
                        end.Timings));
            }
        }
    }

    /// <summary>
    /// Reports the turn's events for one interval. Returns how the turn ended, or
    /// <see langword="null"/> when the interval or the wait passed first.
    /// </summary>
    private static async Task<NhAssistantTurnCompletedEvent?> FollowAsync(
        NhAssistantTurnWatch watch,
        Guid turnId,
        NhAssistantTurnProgressReporter reporter,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var nextRead = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        nextRead.CancelAfter(interval);
        try
        {
            while (await watch.Events.WaitToReadAsync(nextRead.Token))
            {
                while (watch.Events.TryRead(out var evt))
                {
                    reporter.Report(evt, turnId);
                    switch (evt)
                    {
                        case NhAssistantTurnCompletedEvent completed:
                            return completed;
                        case NhAssistantErrorEvent failed:
                            return new NhAssistantTurnCompletedEvent(turnId, NhAssistantTurnStatuses.Failed, new NhAssistantTurnUsage(0, 0, 0), failed.Code);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (nextRead.IsCancellationRequested)
        {
            // Time to read the conversation again; the caller's own cancellation surfaces there.
        }

        return null;
    }

    /// <summary>
    /// The result of a turn that no longer runs, as the conversation shows it: it keeps no outcome per
    /// turn, so the status comes from the conversation and the usage from the turn's message.
    /// </summary>
    private static async Task<NhAssistantConversationTurnResult> ReadEndedTurnAsync(
        IServiceProvider services,
        NhAiInvocationContext caller,
        AssistantConversation conversation)
    {
        var messages = await services.GetRequiredService<INhAssistantStore>()
            .GetMessagesAsync(conversation.Id, NhAssistantConversationReader.MaxMessages, CancellationToken.None);
        var answer = messages.LastOrDefault(message => message.Role == NhAssistantMessageRoles.Assistant);
        var status = conversation.Status switch
        {
            NhAssistantConversationStatuses.WaitingForApproval => NhAssistantTurnStatuses.WaitingForApproval,
            NhAssistantConversationStatuses.Failed => NhAssistantTurnStatuses.Failed,
            _ => NhAssistantTurnStatuses.Completed
        };
        return await ReadResultAsync(
            services,
            caller,
            conversation.Id,
            answer?.TurnId ?? Guid.Empty,
            answer?.Id,
            status,
            null,
            new NhAssistantTurnUsage(answer?.InputTokens ?? 0, answer?.OutputTokens ?? 0, answer?.ToolCalls ?? 0),
            null);
    }

    /// <summary>
    /// The assistant's message of a turn the wait followed from its middle, so it never saw its start.
    /// </summary>
    private static async Task<Guid?> FindAnswerAsync(INhAssistantStore store, Guid conversationId, Guid turnId)
    {
        var messages = await store.GetMessagesAsync(conversationId, NhAssistantConversationReader.MaxMessages, CancellationToken.None);
        return messages.LastOrDefault(message => message.Role == NhAssistantMessageRoles.Assistant && message.TurnId == turnId)?.Id;
    }

    /// <summary>
    /// Reads the page of a channel's message as the endpoint reads <c>clientContext</c>: from the same
    /// JSON, so the turn shows the same screen and the message stores the same context. The endpoint
    /// ignores a context it cannot read and drops entities it cannot keep, so a browser's message never
    /// fails for its page; a channel's code passes the page as a typed value, so what the endpoint would
    /// lose is refused instead of going missing unnoticed.
    /// </summary>
    /// <returns><see langword="false"/> when the endpoint would ignore the context, keep only part of it
    /// or could not store it.</returns>
    private static bool TryReadClientContext(NhAssistantClientContextDto? value, out NhAssistantClientContext? context)
    {
        context = null;
        if (value is null)
        {
            return true;
        }

        // The reader also ignores a context the message could not store.
        var read = NhAssistantClientContext.From(
            JsonSerializer.SerializeToElement(value, NhAssistantJsonSerializerContext.Default.NhAssistantClientContextDto));
        if (read is null || read.Entities.Count != (value.Entities?.Count ?? 0))
        {
            return false;
        }

        context = read;
        return true;
    }

    private static void ValidateOptions(NhAssistantConversationTurnOptions turnOptions)
    {
        ArgumentNullException.ThrowIfNull(turnOptions);
        if (!NhAssistantDecisionChannels.IsValid(turnOptions.Channel))
        {
            throw new ArgumentException(
                $"A channel is a dash-case code of at most {NhAssistantDecisionChannels.MaxLength} characters, such as messaging-chat.",
                nameof(turnOptions));
        }
    }

    private static TaskResult<(NhAiPersonInvocation, NhAssistantConversationAccess)> Failed(string code, string message)
    {
        return TaskResult<(NhAiPersonInvocation, NhAssistantConversationAccess)>.Failed(code, message);
    }

    /// <summary>
    /// A turn whose person and conversation were checked, with the services it runs in and the token
    /// that cancels it: the caller's, or none for a turn that outlives its caller.
    /// </summary>
    private sealed record PreparedTurn(
        IServiceProvider Services,
        NhAiPersonInvocation Person,
        NhAssistantConversationAccess Access,
        string Language,
        CancellationToken TurnCancellation);
}
