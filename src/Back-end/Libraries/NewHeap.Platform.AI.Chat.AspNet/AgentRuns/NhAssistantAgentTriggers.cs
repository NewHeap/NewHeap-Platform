using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Starts agents from application events and from people. Publish an event inside the
/// transaction of the domain change: the background operation joins the transaction, so a
/// rollback never starts a run.
/// </summary>
public interface INhAssistantAgentTriggers
{
    /// <summary>
    /// Fires every trigger registered for <typeparamref name="TEvent"/>.
    /// </summary>
    Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync<TEvent>(
        TEvent evt,
        CancellationToken cancellationToken = default)
        where TEvent : class;

    /// <summary>
    /// Starts an agent on a subject for a person, who becomes the run's initiator. The caller must
    /// already have checked that the person may start the agent.
    /// </summary>
    Task<NhAssistantTriggerReceipt> StartAsync(
        string agentId,
        NhAiSubjectRef subject,
        string initiatorActorId,
        string? message,
        CancellationToken cancellationToken = default);
}

/// <param name="TriggerId">The trigger that fired.</param>
/// <param name="Outcome">One of <see cref="NhAssistantTriggerOutcomes"/>.</param>
/// <param name="RunId">The run that received the event, when there is one.</param>
/// <param name="ConversationId">The agent thread.</param>
/// <param name="ReasonCode">Why the trigger was rejected, ignored or deduplicated.</param>
public sealed record NhAssistantTriggerReceipt(
    string TriggerId,
    string Outcome,
    Guid? RunId,
    Guid? ConversationId,
    string? ReasonCode);

public static class NhAssistantTriggerOutcomes
{
    public const string Started = "started";
    public const string Queued = "queued";
    public const string Coalesced = "coalesced";
    public const string Superseded = "superseded";
    public const string Ignored = "ignored";
    public const string Deduplicated = "deduplicated";
    public const string Rejected = "rejected";
}

public static class NhAssistantTriggerReasons
{
    public const string RateLimited = "rate-limited";
    public const string Busy = "busy";
    public const string Deduplicated = "deduplicated";
}

internal sealed record NhAssistantTriggerFire(
    string TriggerId,
    string AgentId,
    NhAiSubjectRef Subject,
    string? InitiatorActorId,
    string Message,
    string? EventKey,
    NhAssistantTriggerConflict Conflict,
    TimeSpan Debounce);

internal sealed class NhAssistantAgentTriggers(
    NhAssistantRegistrationState registration,
    NhAssistantAgentCatalog agents,
    NhAssistantSubjectAccessService subjects,
    NhAssistantThreadService threads,
    NhAssistantRunStore runs,
    NhAssistantAgentEvents events,
    IServiceProvider services,
    ILogger<NhAssistantAgentTriggers> logger) : INhAssistantAgentTriggers
{
    /// <summary>
    /// Background operations are needed only once an agent runs; hosts without agents do not
    /// register them.
    /// </summary>
    private INhBackgroundOperationService Operations => services.GetRequiredService<INhBackgroundOperationService>();

    public async Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync<TEvent>(
        TEvent evt,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(evt);
        var receipts = new List<NhAssistantTriggerReceipt>();
        foreach (var trigger in registration.Triggers.Values.Where(trigger =>
                     trigger.Source == NhAssistantTriggerSource.Event && trigger.EventType == typeof(TEvent)))
        {
            var fire = new NhAssistantTriggerFire(
                trigger.Id,
                trigger.AgentId,
                trigger.Subject(evt),
                trigger.Initiator(evt),
                trigger.Message(evt),
                trigger.Dedupe(evt),
                trigger.Conflict,
                trigger.Debounce);
            receipts.Add(await FireAsync(fire, cancellationToken));
        }

        return receipts;
    }

    public Task<NhAssistantTriggerReceipt> StartAsync(
        string agentId,
        NhAiSubjectRef subject,
        string initiatorActorId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatorActorId);
        return FireAsync(
            new NhAssistantTriggerFire(
                NhAssistantTriggerIds.Manual,
                agentId,
                subject,
                initiatorActorId,
                string.IsNullOrWhiteSpace(message) ? "Start working on this subject." : message.Trim(),
                null,
                NhAssistantTriggerConflict.Queue,
                TimeSpan.Zero),
            cancellationToken);
    }

    /// <summary>
    /// Starts a follow-up run after a proposal was rejected or finished executing, so the agent
    /// continues with the outcome. An approved proposal follows up once its execution ended.
    /// </summary>
    internal Task<NhAssistantTriggerReceipt> StartFollowUpAsync(
        AssistantThread thread,
        AssistantApproval approval,
        string? actorId,
        CancellationToken cancellationToken)
    {
        var executed = approval.Status is not (NhAssistantApprovalStatuses.Approved or NhAssistantApprovalStatuses.Rejected);
        var outcome = approval.Status switch
        {
            NhAssistantApprovalStatuses.Rejected => "rejected",
            NhAssistantApprovalStatuses.Executed => "approved and executed",
            NhAssistantApprovalStatuses.ExecutionFailed => "approved, but its execution failed",
            NhAssistantApprovalStatuses.ExecutionUnknown => "approved, but the outcome of its execution is unknown",
            NhAssistantApprovalStatuses.Stale => "approved, but closed as stale because the subject changed",
            _ => approval.Status
        };
        return FireAsync(
            new NhAssistantTriggerFire(
                executed ? NhAssistantTriggerIds.ProposalExecuted : NhAssistantTriggerIds.ProposalDecided,
                thread.AgentId,
                new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                actorId,
                $"Proposal {approval.ProposalId:N} for tool {approval.ToolId} was {outcome}.",
                approval.Id.ToString("N"),
                NhAssistantTriggerConflict.Coalesce,
                TimeSpan.FromSeconds(30)),
            cancellationToken);
    }

    /// <summary>
    /// Fires a scheduled trigger for one subject of one tick.
    /// </summary>
    internal Task<NhAssistantTriggerReceipt> FireScheduledAsync(
        NhAssistantTriggerDefinition trigger,
        NhAssistantScheduledTick tick,
        CancellationToken cancellationToken)
    {
        return FireAsync(Create(trigger, tick, initiatorActorId: null), cancellationToken);
    }

    /// <summary>
    /// Fires a webhook trigger for one verified delivery whose body names a subject.
    /// </summary>
    internal Task<NhAssistantTriggerReceipt> FireWebhookAsync(
        NhAssistantTriggerDefinition trigger,
        NhAssistantWebhookDelivery delivery,
        CancellationToken cancellationToken)
    {
        return FireAsync(Create(trigger, delivery, initiatorActorId: null), cancellationToken);
    }

    private static NhAssistantTriggerFire Create(NhAssistantTriggerDefinition trigger, object evt, string? initiatorActorId)
    {
        return new NhAssistantTriggerFire(
            trigger.Id,
            trigger.AgentId,
            trigger.Subject(evt),
            initiatorActorId,
            trigger.Message(evt),
            trigger.Dedupe(evt),
            trigger.Conflict,
            trigger.Debounce);
    }

    internal async Task<NhAssistantTriggerReceipt> FireAsync(
        NhAssistantTriggerFire fire,
        CancellationToken cancellationToken)
    {
        var receipt = await FireCoreAsync(fire, cancellationToken);
        var accepted = receipt.Outcome is NhAssistantTriggerOutcomes.Started
            or NhAssistantTriggerOutcomes.Queued
            or NhAssistantTriggerOutcomes.Coalesced
            or NhAssistantTriggerOutcomes.Superseded;
        await events.TriggerAsync(
            fire.AgentId,
            receipt.ConversationId,
            fire.TriggerId,
            receipt.RunId,
            fire.Subject,
            fire.InitiatorActorId,
            accepted ? receipt.Outcome : receipt.ReasonCode ?? receipt.Outcome,
            accepted,
            cancellationToken);
        return receipt;
    }

    private async Task<NhAssistantTriggerReceipt> FireCoreAsync(
        NhAssistantTriggerFire fire,
        CancellationToken cancellationToken)
    {
        if (!registration.AgentRuns.TryGetValue(fire.AgentId, out var options))
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentNotConfigured);
        }
        if (!options.Enabled)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentDisabled);
        }

        var agent = await agents.FindAsync(fire.AgentId, includeDisabled: false, cancellationToken);
        if (agent is null)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentDisabled);
        }

        var subject = await subjects.DescribeAsync(fire.Subject, cancellationToken);
        if (subject is null)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.SubjectNotFound);
        }

        var accountable = threads.ResolveAccountableOwner(agent.Definition, subject);
        if (!accountable.Success)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.NoAccountableOwner);
        }

        var created = await threads.GetOrCreateAsync(agent, subject, cancellationToken);
        if (!created.Success)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.NoAccountableOwner);
        }

        var thread = created.Data!;
        var conversationId = thread.ConversationId;
        if (fire.EventKey is { } eventKey
            && await runs.FindRunByEventKeyAsync(conversationId, fire.TriggerId, eventKey, cancellationToken) is { } duplicate)
        {
            return new NhAssistantTriggerReceipt(
                fire.TriggerId,
                NhAssistantTriggerOutcomes.Deduplicated,
                duplicate.Id,
                conversationId,
                NhAssistantTriggerReasons.Deduplicated);
        }

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        if (await runs.CountRunsSinceAsync(conversationId, since, cancellationToken) >= options.MaxRunsPerSubjectPerDay)
        {
            return Rejected(fire, conversationId, NhAssistantTriggerReasons.RateLimited);
        }

        var queued = await runs.FindQueuedRunAsync(conversationId, cancellationToken);
        var active = await runs.FindActiveRunAsync(conversationId, cancellationToken);
        if (fire.Conflict == NhAssistantTriggerConflict.Ignore && active is not null)
        {
            logger.LogInformation(
                "Trigger {TriggerId} for agent {AgentId} was ignored because a run is active.",
                fire.TriggerId,
                fire.AgentId);
            return new NhAssistantTriggerReceipt(
                fire.TriggerId,
                NhAssistantTriggerOutcomes.Ignored,
                active.Id,
                conversationId,
                NhAssistantTriggerReasons.Busy);
        }

        var input = CreateInput(fire, conversationId);
        if (fire.Conflict == NhAssistantTriggerConflict.Coalesce && queued is not null)
        {
            // The pending run claims every waiting input when it starts.
            await runs.AddQueuedInputAsync(input, cancellationToken);
            return new NhAssistantTriggerReceipt(
                fire.TriggerId,
                NhAssistantTriggerOutcomes.Coalesced,
                queued.Id,
                conversationId,
                null);
        }

        var superseded = false;
        if (fire.Conflict == NhAssistantTriggerConflict.Supersede && active?.BackgroundOperationId is { } activeOperationId)
        {
            var cancelled = await Operations.RequestCancellationForActorAsync(
                activeOperationId,
                OwnerActor(thread, accountable.Data!, agent.Definition),
                cancellationToken);
            superseded = cancelled.Success;
        }

        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TriggerId = fire.TriggerId,
            TriggerEventKey = fire.EventKey,
            InitiatorActorId = fire.InitiatorActorId,
            AccountableOwnerId = accountable.Data!,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            Shadow = options.Shadow,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await runs.AddRunAsync(run, cancellationToken);
        await runs.AddQueuedInputAsync(input, cancellationToken);

        var owner = OwnerActor(thread, accountable.Data!, agent.Definition);
        var notBefore = fire.Conflict == NhAssistantTriggerConflict.Coalesce && fire.Debounce > TimeSpan.Zero
            ? DateTimeOffset.UtcNow.Add(fire.Debounce)
            : (DateTimeOffset?)null;
        var enqueued = await Operations.EnqueueAsync(
            new NhAssistantAgentRunRequest(run.Id),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = owner.UserId,
                OwnerAgentKey = owner.Key,
                DivisionId = subject.DivisionId,
                IdempotencyKey = run.Id.ToString("N"),
                ConcurrencyKey = $"agent:{fire.AgentId}:subject:{subject.ScopeKey}:{subject.Subject.Type}:{subject.Subject.Id}",
                ConflictBehavior = NhBackgroundOperationConflictBehavior.Queue,
                DomainObjectType = subject.Subject.Type,
                DomainObjectId = subject.Subject.Id,
                CorrelationId = run.Id.ToString("N"),
                NotBefore = notBefore
            },
            cancellationToken);
        if (!enqueued.Success)
        {
            logger.LogWarning(
                "Trigger {TriggerId} for agent {AgentId} could not enqueue run {RunId}.",
                fire.TriggerId,
                fire.AgentId,
                run.Id);
            await runs.TrySetRunStatusAsync(
                run.Id,
                [NhAssistantRunStatuses.Queued],
                NhAssistantRunStatuses.Failed,
                CancellationToken.None,
                NhAssistantRunFailureClasses.Internal,
                NhAssistantRunStore.OrphanedRunCode);
            return Rejected(fire, conversationId, NhAssistantRunStore.OrphanedRunCode);
        }

        await runs.SetRunAttemptAsync(run.Id, 0, enqueued.Data!.Id, cancellationToken);
        var outcome = superseded
            ? NhAssistantTriggerOutcomes.Superseded
            : active is null ? NhAssistantTriggerOutcomes.Started : NhAssistantTriggerOutcomes.Queued;
        return new NhAssistantTriggerReceipt(fire.TriggerId, outcome, run.Id, conversationId, null);
    }

    /// <summary>
    /// The background-operation owner of a run: the agent when it answers for itself, otherwise the
    /// accountable user.
    /// </summary>
    internal static NhBackgroundOperationActor OwnerActor(
        AssistantThread thread,
        string accountableOwnerId,
        NhAssistantAgentDefinition agent)
    {
        if (!string.Equals(thread.Accountability, NhAssistantAccountabilities.Agent, StringComparison.Ordinal)
            && Guid.TryParse(accountableOwnerId, out var userId))
        {
            return NhBackgroundOperationActor.ForUser(userId);
        }

        return NhBackgroundOperationActor.ForAgent(agent.ActorId);
    }

    private AssistantQueuedInput CreateInput(NhAssistantTriggerFire fire, Guid conversationId)
    {
        var personal = fire.TriggerId is NhAssistantTriggerIds.Manual
            or NhAssistantTriggerIds.QueuedMessage
            or NhAssistantTriggerIds.AgentRequest;
        var text = fire.Message.Length > registration.Limits.MaxMessageChars
            ? fire.Message[..registration.Limits.MaxMessageChars]
            : fire.Message;
        return new AssistantQueuedInput
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Kind = personal ? NhAssistantQueuedInputKinds.Message : NhAssistantQueuedInputKinds.Event,
            AuthorActorId = personal ? fire.InitiatorActorId : null,
            TriggerId = fire.TriggerId,
            Text = text,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private NhAssistantTriggerReceipt Rejected(NhAssistantTriggerFire fire, Guid? conversationId, string reason)
    {
        logger.LogInformation(
            "Trigger {TriggerId} for agent {AgentId} was rejected ({Reason}).",
            fire.TriggerId,
            fire.AgentId,
            reason);
        return new NhAssistantTriggerReceipt(fire.TriggerId, NhAssistantTriggerOutcomes.Rejected, null, conversationId, reason);
    }
}
