using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Continues a person's chat after the application reported the execution of a proposal approved there,
/// for a tool declared with <see cref="NhAssistantApplicationExecutionBuilder.ContinueChatAfterReport"/>
/// (FB61). A report asks for one follow-up turn of the chat; the turn starts after
/// <see cref="NhAssistantTiming.ChatFollowUpDelay"/> on the application's clock, so reports that arrive
/// together reach one turn. When it
/// starts, the chat must be idle or failed and its newest message must still be the application's, and the
/// person whose turn made the proposal must still use the chat and the agent: their principal is rebuilt
/// with the application's <see cref="INhAiPrincipalFactory"/>, resolved with its context resolver in the
/// active division that turn worked in, and checked against the assistant's access policy and the agent's
/// policy, as <see cref="INhAssistantConversationTurns"/> checks a person the application vouches for. The
/// turn runs as that person and can only propose (<see cref="INhAssistantTurnRunner.StartFollowUpTurnAsync"/>).
/// Each start and each skip is audited (<see cref="NhAssistantAuditEventKind.ChatFollowUpStarted"/>,
/// <see cref="NhAssistantAuditEventKind.ChatFollowUpSkipped"/>). The wait lives in this process: a follow-up
/// whose host stops before it starts is not started elsewhere, and the report stays in the chat.
/// </summary>
internal sealed class NhAssistantChatFollowUps(
    IServiceScopeFactory scopeFactory,
    IServiceProvider services,
    ILogger<NhAssistantChatFollowUps> logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly HashSet<Task> _work = [];
    private readonly TimeSpan _delay = (services.GetService<NhAssistantTiming>() ?? NhAssistantTiming.Default).ChatFollowUpDelay;

    /// <summary>
    /// The application's clock (a registered <see cref="TimeProvider"/>, otherwise the system clock), which
    /// the wait follows as every debounce does.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    private bool _stopped;

    /// <summary>
    /// Asks for the follow-up turn of <paramref name="conversationId"/> after the report of
    /// <paramref name="approvalId"/>. A follow-up that waits already takes it along: the turn reads every
    /// report in the chat's history, and runs as the person of the first one.
    /// </summary>
    public void Request(Guid conversationId, Guid approvalId)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            if (_pending.ContainsKey(conversationId))
            {
                logger.LogDebug(
                    "The report of proposal {ApprovalId} joins the follow-up turn that chat {ConversationId} waits for.",
                    approvalId,
                    conversationId);
                return;
            }

            var pending = new Pending(conversationId, approvalId);
            _pending.Add(conversationId, pending);
            Task running;
            // The follow-up belongs to no request: nothing of the reporting call flows into it.
            using (ExecutionContext.SuppressFlow())
            {
                running = Task.Run(() => RunAsync(pending));
            }

            _work.Add(running);
            running.ContinueWith(
                done => Forget(done),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Starts every follow-up that waits now and returns once they and the turns they started ended, for
    /// tests that do not wait for <see cref="NhAssistantTiming.ChatFollowUpDelay"/>.
    /// </summary>
    internal async Task RunPendingAsync()
    {
        while (true)
        {
            Task[] work;
            lock (_gate)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.Release.Cancel();
                }

                work = [.. _work];
            }

            if (work.Length == 0)
            {
                return;
            }

            await Task.WhenAll(work);
        }
    }

    /// <summary>
    /// Starts nothing more once the host stops; a follow-up that waits ends without a turn.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var pending in _pending.Values)
            {
                pending.Release.Cancel();
            }
        }
    }

    private async Task RunAsync(Pending pending)
    {
        try
        {
            await Task.Delay(_delay, _applicationClock, pending.Release.Token);
        }
        catch (OperationCanceledException)
        {
            // Started early, or the host stops.
        }

        lock (_gate)
        {
            _pending.Remove(pending.ConversationId);
            pending.Release.Dispose();
            if (_stopped)
            {
                return;
            }
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await StartAsync(scope.ServiceProvider, pending.ConversationId, pending.ApprovalId);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A model or provider failure can carry content: the type only.
            logger.LogWarning(
                "The follow-up turn of chat {ConversationId} after the report of proposal {ApprovalId} failed ({ExceptionType}).",
                pending.ConversationId,
                pending.ApprovalId,
                exception.GetType().Name);
        }
    }

    private void Forget(Task done)
    {
        lock (_gate)
        {
            _work.Remove(done);
        }
    }

    /// <summary>
    /// Checks the chat and the person again now that the follow-up would start, starts the turn as that
    /// person and waits until it ended.
    /// </summary>
    private async Task StartAsync(IServiceProvider scoped, Guid conversationId, Guid approvalId)
    {
        var store = scoped.GetRequiredService<INhAssistantStore>();
        var approval = await store.FindApprovalAsync(approvalId, CancellationToken.None);
        var conversation = await store.FindShareableConversationAsync(conversationId, CancellationToken.None);
        if (approval is null || conversation is null)
        {
            // The chat was archived or removed in the meantime: nothing to continue.
            logger.LogInformation("Chat {ConversationId} is gone; its follow-up turn does not start.", conversationId);
            return;
        }

        var events = scoped.GetRequiredService<NhAssistantAgentEvents>();
        var actorId = approval.InitiatorActorId;
        // The turn speaks in the language of the person's turn that approved the proposal.
        using var culture = NhAssistantRunCulture.Use(scoped.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value, approval.DecisionCulture);
        var skip = await SkipReasonAsync(scoped, store, conversation, actorId);
        if (skip is not null)
        {
            await SkippedAsync(events, approval, conversation, actorId, skip);
            return;
        }

        var person = await ResolvePersonAsync(scoped, conversation, approval, actorId!);
        if (!person.Success)
        {
            await SkippedAsync(events, approval, conversation, actorId, NhAssistantResults.CodeOf(person, NhAssistantErrorCodes.ContextUnavailable));
            return;
        }

        var turnId = Guid.NewGuid();
        var started = await scoped.GetRequiredService<INhAssistantTurnRunner>().StartFollowUpTurnAsync(
            new NhAssistantFollowUpTurnRequest(
                conversationId,
                turnId,
                person.Data.Context,
                person.Data,
                NhAssistantPersonCalls.LanguageOf(CultureInfo.CurrentUICulture.Name))
            {
                Channel = NhAssistantDecisionChannels.ExecutionReport
            },
            CancellationToken.None);
        if (!started.Success)
        {
            await SkippedAsync(events, approval, conversation, actorId, NhAssistantResults.CodeOf(started, NhAssistantErrorCodes.TurnFailed));
            return;
        }

        await events.ChatFollowUpAsync(
            approval,
            conversation.AgentId,
            conversation.AgentVersion,
            actorId!,
            turnId,
            approval.Status,
            NhAssistantDecisionChannels.ExecutionReport,
            CancellationToken.None);
        logger.LogInformation(
            "The report of proposal {ApprovalId} started follow-up turn {TurnId} in chat {ConversationId}.",
            approval.Id,
            turnId,
            conversationId);

        // The live updates carry the turn to the people of the chat; this waits only for its end.
        await foreach (var _ in started.Data.Events.ReadAllAsync(CancellationToken.None))
        {
        }

        await started.Data.Completion;
    }

    /// <summary>
    /// Why the chat is not continued now, before the person is resolved: the assistant is off, the chat
    /// runs a turn or waits for a decision or an answer, a person or a turn went on since the report, or
    /// nobody's turn made the proposal. <see langword="null"/> when it may continue.
    /// </summary>
    private static async Task<string?> SkipReasonAsync(
        IServiceProvider scoped,
        INhAssistantStore store,
        AssistantConversation conversation,
        string? actorId)
    {
        if (!scoped.GetRequiredService<IOptionsMonitor<NhAssistantOptions>>().CurrentValue.Enabled)
        {
            return NhAssistantErrorCodes.Disabled;
        }
        if (await scoped.GetRequiredService<NhAssistantRunStore>().FindThreadByConversationAsync(conversation.Id, CancellationToken.None) is not null)
        {
            return NhAssistantErrorCodes.ConversationNotFound;
        }
        if (conversation.Status is NhAssistantConversationStatuses.Running or NhAssistantConversationStatuses.WaitingForApproval)
        {
            return conversation.Status;
        }

        var newest = (await store.GetMessagesAsync(conversation.Id, 1, CancellationToken.None)).LastOrDefault();
        if (newest?.Role != NhAssistantMessageRoles.Application)
        {
            return NhAssistantApplicationExecutionCodes.ChatAnswered;
        }

        return string.IsNullOrWhiteSpace(actorId) ? NhAssistantErrorCodes.ContextUnavailable : null;
    }

    /// <summary>
    /// The person whose turn made the proposal, as their own message would bring them: their principal
    /// rebuilt by the application's principal factory, resolved by its context resolver in the active
    /// division of that turn when they may still use it, in their chat, and passing the assistant's access
    /// policy and the agent's policy. Never the person who decided, nor the chat's owner on behalf of a
    /// participant.
    /// </summary>
    private static async Task<TaskResult<NhAiPersonInvocation>> ResolvePersonAsync(
        IServiceProvider scoped,
        AssistantConversation conversation,
        AssistantApproval approval,
        string actorId)
    {
        var divisionId = DivisionOf(scoped, approval);
        var subject = await SubjectOfAsync(scoped, conversation, divisionId);
        var principal = scoped.GetService<INhAiPrincipalFactory>() is { } principals
            ? await principals.CreateAsync(actorId, subject, CancellationToken.None)
            : null;
        if (principal is null)
        {
            // An unknown person, or one who may no longer sign in, lends no rights.
            return Failed(NhAssistantErrorCodes.ContextUnavailable, "The person's principal is not available.");
        }

        var resolved = await NhAiPersonInvocations.ResolveAsync(scoped, principal, divisionId, CancellationToken.None);
        if (!resolved.Success || !string.Equals(resolved.Data.Context.ActorId, actorId, StringComparison.Ordinal))
        {
            return Failed(NhAssistantErrorCodes.ContextUnavailable, "The person's invocation context is not available.");
        }

        var person = resolved.Data;
        var chat = await NhAssistantPersonCalls.FindChatAsync(scoped, person.Context, conversation.Id, CancellationToken.None);
        if (!chat.Success)
        {
            return TaskResult<NhAiPersonInvocation>.Failed(chat);
        }

        var effective = await scoped.GetRequiredService<NhAssistantAgentCatalog>()
            .FindAsync(conversation.AgentId, includeDisabled: false, CancellationToken.None);
        if (effective is null)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
        }

        var deciding = new NhAssistantDecidingPerson(principal, actorId, person.Context.TenantId);
        if (!await NhAssistantPersonCalls.PassesAccessPolicyAsync(scoped, deciding, person)
            || !await scoped.GetRequiredService<NhAssistantAgentAccess>().CanUseAsync(principal, effective.Definition))
        {
            return Failed(NhAssistantErrorCodes.AgentForbidden, "The person may not use the assistant agent.");
        }

        return TaskResult<NhAiPersonInvocation>.Succeeded(person);
    }

    /// <summary>
    /// The active division the person's turn worked in when it made the proposal, as the proposal's
    /// targets name it; <see langword="null"/> when the host maps no active division or the turn had none.
    /// The person's division policy is checked again when they are resolved.
    /// </summary>
    private static Guid? DivisionOf(IServiceProvider scoped, AssistantApproval approval)
    {
        if (scoped.GetService<NhAiAspNetRegistrationState>()?.ActiveDivision is not { } activeDivision)
        {
            return null;
        }

        foreach (var target in NhAssistantProposalSerializer.Deserialize(approval.ProposalJson).Targets)
        {
            if (string.Equals(target.Type, activeDivision.ScopeType, StringComparison.Ordinal)
                && Guid.TryParse(target.Id, out var divisionId))
            {
                return divisionId;
            }
        }

        return null;
    }

    /// <summary>
    /// What the principal factory rebuilds the person for: the chat's subject, otherwise the whole
    /// application in the chat's tenant and the turn's division.
    /// </summary>
    private static async Task<NhAiSubjectInfo> SubjectOfAsync(IServiceProvider scoped, AssistantConversation conversation, Guid? divisionId)
    {
        if (NhAssistantChatSubjects.SubjectOf(conversation) is { } subject
            && await scoped.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subject, CancellationToken.None) is { } described)
        {
            return described;
        }

        return new NhAiSubjectInfo(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(conversation.TenantId, divisionId))
        {
            TenantId = conversation.TenantId,
            DivisionId = divisionId
        };
    }

    private static Task SkippedAsync(
        NhAssistantAgentEvents events,
        AssistantApproval approval,
        AssistantConversation conversation,
        string? actorId,
        string reason)
    {
        return events.ChatFollowUpAsync(
            approval,
            conversation.AgentId,
            conversation.AgentVersion,
            string.IsNullOrWhiteSpace(actorId) ? NhAssistantSpeakers.ApplicationActorId : actorId,
            null,
            reason,
            NhAssistantDecisionChannels.ExecutionReport,
            CancellationToken.None);
    }

    private static TaskResult<NhAiPersonInvocation> Failed(string code, string message)
    {
        return TaskResult<NhAiPersonInvocation>.Failed(code, message);
    }

    /// <summary>
    /// A follow-up that waits for <see cref="NhAssistantTiming.ChatFollowUpDelay"/>; <see cref="Release"/>
    /// ends the wait early.
    /// </summary>
    private sealed class Pending(Guid conversationId, Guid approvalId)
    {
        public Guid ConversationId { get; } = conversationId;

        public Guid ApprovalId { get; } = approvalId;

        public CancellationTokenSource Release { get; } = new();
    }
}
