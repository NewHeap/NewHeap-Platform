using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Sends a person's message to their chat and decides the approval their turn waits for, for the
/// application's own channels, such as a messaging app in which a person talks with the assistant. It
/// is a server API for the application's code; there is no endpoint for it. The application vouches for
/// the person, as for <see cref="INhAssistantProposalDecisionService"/>: it authenticated them through
/// its channel and passes their principal, actor id and tenant, as the assistant's context resolver
/// gives them for the person's own requests.
/// <para>
/// A turn is the turn of <c>POST conversations/{id}/messages</c> and
/// <c>POST conversations/{id}/approvals/{approvalId}/decide</c>, through the same runner, with the same
/// access rules: the person's conversation in their tenant, the assistant's access policy, the agent's
/// own policy, the decision rules and the person's tools. It runs without a request of the person, in
/// services of its own and without the caller's request: an invocation scope of the person makes their
/// principal and invocation context ambient, so the person's tools authorize that principal, never the
/// caller's request. Tools of the API bridge need the person's own request and are not offered.
/// </para>
/// <para>
/// Only chats are served; an agent's thread answers <c>assistant-conversation-not-found</c>, as an agent
/// kept out of the chat reads as unknown. The audit records the channel on the turn's events.
/// </para>
/// </summary>
public interface INhAssistantConversationTurns
{
    /// <summary>
    /// Sends <paramref name="message"/> as <paramref name="person"/> and returns the turn's result once it
    /// ended or waits for an approval.
    /// <para>
    /// Fails with the endpoint's codes: <c>assistant-disabled</c>; <c>assistant-context-unavailable</c>
    /// when the application's resolver refuses the principal or resolves another actor or tenant than the
    /// person names; <c>assistant-conversation-not-found</c> for a conversation the person does not take
    /// part in, of another tenant, or an agent's thread; <c>assistant-agent-not-found</c>;
    /// <c>assistant-agent-forbidden</c> when the person fails the assistant's access policy or the agent's
    /// policy; <c>assistant-conversation-busy</c>, <c>assistant-message-invalid</c>,
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
    NhAssistantUsageDto Usage);

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
        ValidatePerson(person);
        ArgumentNullException.ThrowIfNull(message);
        ValidateOptions(options);

        return RunAsync(
            person,
            conversationId,
            options,
            async (turn, token) =>
            {
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
                        turn.Language)
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
        ValidatePerson(person);
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
        Task<TaskResult<NhAssistantConversationTurnResult>> turn;
        using (ExecutionContext.SuppressFlow())
        {
            turn = Task.Run(() => RunWithoutCallerAsync(person, conversationId, turnOptions, keep, start, cancellationToken));
        }

        return await turn.WaitAsync(cancellationToken);
    }

    private async Task<TaskResult<NhAssistantConversationTurnResult>> RunWithoutCallerAsync(
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationTurnOptions turnOptions,
        bool keep,
        Func<PreparedTurn, CancellationToken, Task<TaskResult<NhAssistantTurnHandle>>> start,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var language = LanguageOf(turnOptions.Language);
        UseCulture(services, language);

        var prepared = await PrepareAsync(services, person, conversationId, turnOptions, cancellationToken);
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
                await CollectAsync(services, personInvocation.Context, conversationId, started.Data));
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
    private async Task<TaskResult<(NhAiPersonInvocation Person, NhAssistantConversationAccess Access)>> PrepareAsync(
        IServiceProvider services,
        NhAssistantDecidingPerson person,
        Guid conversationId,
        NhAssistantConversationTurnOptions turnOptions,
        CancellationToken cancellationToken)
    {
        var resolved = await NhAiPersonInvocations.ResolveAsync(services, person.User, turnOptions.ActiveDivisionId, cancellationToken);
        // The application names the person; their own principal must say the same, or the turn would
        // act in one person's conversation with another person's rights.
        if (!resolved.Success
            || !string.Equals(resolved.Data.Context.ActorId, person.ActorId, StringComparison.Ordinal)
            || !string.Equals(resolved.Data.Context.TenantId, person.TenantId, StringComparison.Ordinal))
        {
            return Failed(NhAssistantErrorCodes.ContextUnavailable, "The person's invocation context is not available.");
        }

        var personInvocation = resolved.Data;
        var caller = personInvocation.Context;
        if (await services.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync(conversationId, cancellationToken) is not null)
        {
            // An agent's thread follows its subject's access and runs; this service serves chats only.
            return Failed(NhAssistantErrorCodes.ConversationNotFound, "The assistant conversation was not found.");
        }

        var access = await services.GetRequiredService<INhAssistantStore>().FindAccessAsync(
            conversationId,
            caller.ActorId,
            caller.TenantId,
            cancellationToken);
        if (access is null)
        {
            return Failed(NhAssistantErrorCodes.ConversationNotFound, "The assistant conversation was not found.");
        }

        var effective = await services.GetRequiredService<NhAssistantAgentCatalog>()
            .FindAsync(access.Conversation.AgentId, includeDisabled: false, cancellationToken);
        if (effective is null)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
        }

        // Every endpoint requires the assistant's access policy, and a turn the agent's own policy.
        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(
            services.GetRequiredService<NhAssistantRegistrationState>(),
            options.CurrentValue);
        var authorization = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(person.User, personInvocation.AuthorizationResource, accessPolicy);
        if (!authorization.Succeeded
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
    private static async Task<NhAssistantConversationTurnResult> CollectAsync(
        IServiceProvider services,
        NhAiInvocationContext caller,
        Guid conversationId,
        NhAssistantTurnHandle handle)
    {
        var turnId = Guid.Empty;
        Guid? messageId = null;
        var status = NhAssistantTurnStatuses.Failed;
        string? errorCode = null;
        var usage = new NhAssistantTurnUsage(0, 0, 0);
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
                    break;
                case NhAssistantErrorEvent failed:
                    status = NhAssistantTurnStatuses.Failed;
                    errorCode = failed.Code;
                    break;
            }
        }
        await handle.Completion;

        // The conversation as it stands after the turn, as the person reads it.
        var access = await services.GetRequiredService<INhAssistantStore>()
            .FindAccessAsync(conversationId, caller.ActorId, caller.TenantId, CancellationToken.None);
        NhAssistantMessageDto? message = null;
        NhAssistantApprovalPartDto[] pending = [];
        if (access is not null)
        {
            var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(access, CancellationToken.None);
            var conversation = NhAssistantDtoMapper.ToDto(view, caller.ActorId);
            message = conversation.Messages.FirstOrDefault(item => item.Id == messageId);
            pending = conversation.PendingApproval is NhAssistantApprovalPartDto approval ? [approval] : [];
        }

        return new NhAssistantConversationTurnResult(
            conversationId,
            turnId,
            status,
            errorCode,
            message,
            pending,
            new NhAssistantUsageDto(usage.InputTokens, usage.OutputTokens, usage.ToolCalls));
    }

    /// <summary>
    /// Runs the turn in the application's supported culture of its language, as the endpoints run a
    /// request that names only a language.
    /// </summary>
    private static void UseCulture(IServiceProvider services, string language)
    {
        var supported = services.GetService<IOptions<RequestLocalizationOptions>>()?.Value.SupportedUICultures ?? [];
        var culture = supported.FirstOrDefault(item =>
            string.Equals(item.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase));
        if (culture is null)
        {
            return;
        }

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    private static string LanguageOf(string? language)
    {
        return language is not null && language.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";
    }

    private static void ValidatePerson(NhAssistantDecidingPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(person.User, nameof(person));
        ArgumentException.ThrowIfNullOrWhiteSpace(person.ActorId, nameof(person));
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
