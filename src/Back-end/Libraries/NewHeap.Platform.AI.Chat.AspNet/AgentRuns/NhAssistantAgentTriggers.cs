using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Starts agents from application events and from people. Publish an event inside the
/// transaction of the domain change, on the context that holds the background operations: the
/// trigger is then recorded as a background operation that fires once the transaction committed,
/// so a rollback leaves nothing behind and a committed change always reaches its agent. Published
/// after the commit, a trigger fires at once.
/// </summary>
public interface INhAssistantAgentTriggers
{
    /// <summary>
    /// Fires every trigger registered for <typeparamref name="TEvent"/>, and queues the proposal
    /// revalidations registered for it with <c>AddProposalRevalidation</c> when an agent has an open
    /// proposal about the event's subject or objects. Inside the application's transaction each
    /// trigger and revalidation is a background operation that runs only once that transaction
    /// committed, and the receipts read <see cref="NhAssistantTriggerOutcomes.Deferred"/>.
    /// <para>
    /// Each agent's state is read once per call before any of its trigger's lambdas run: with the
    /// assistant disabled every receipt is rejected with <c>assistant-disabled</c> and nothing is
    /// queued; a stopped or code-disabled agent's triggers are rejected, audited once per call and
    /// agent; a trigger's <c>When</c> filter decides next. A paused agent's events wait in their
    /// thread (<see cref="NhAssistantTriggerOutcomes.Waiting"/>) until it resumes.
    /// </para>
    /// <para>
    /// Every trigger also fires for the shadow variants of its agent (<c>ShadowOf</c>), with their
    /// own state; the receipts are those of the live agents.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<NhAssistantTriggerReceipt>> PublishAsync<TEvent>(
        TEvent evt,
        CancellationToken cancellationToken = default)
        where TEvent : class;

    /// <summary>
    /// Starts an agent on a subject for a person, who becomes the run's initiator. The caller must
    /// already have checked that the person may start the agent. Inside the application's
    /// transaction the start is deferred until the commit, as in <see cref="PublishAsync{TEvent}"/>.
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
/// <param name="ConversationId">The agent thread; <see langword="null"/> for a deferred trigger.</param>
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

    /// <summary>
    /// The agent is paused: the event waits in its thread, deduplicated by its dedupe key, and
    /// one run per thread claims the waiting events when the agent resumes.
    /// </summary>
    public const string Waiting = "waiting";

    /// <summary>
    /// Published inside the application's transaction: the trigger fires once the transaction
    /// committed, and not at all after a rollback. The receipt names no run or thread yet.
    /// </summary>
    public const string Deferred = "deferred";
}

public static class NhAssistantTriggerReasons
{
    public const string RateLimited = "rate-limited";
    public const string Busy = "busy";
    public const string Deduplicated = "deduplicated";

    /// <summary>
    /// The trigger's <c>When</c> filter refused the event; the outcome is
    /// <see cref="NhAssistantTriggerOutcomes.Ignored"/> and nothing is audited.
    /// </summary>
    public const string Filtered = "filtered";
}

internal sealed record NhAssistantTriggerFire(
    string TriggerId,
    string AgentId,
    NhAiSubjectRef Subject,
    string? InitiatorActorId,
    string Message,
    string? EventKey,
    NhAssistantTriggerConflict Conflict,
    TimeSpan Debounce)
{
    /// <summary>
    /// The payload the trigger stores with the event for the agent's rule handler, as JSON.
    /// </summary>
    public string? PayloadJson { get; init; }

    /// <summary>
    /// The id of the input this fire writes. A deferred fire carries it, so a retried operation
    /// finds the input it wrote and fires only once.
    /// </summary>
    public Guid? InputId { get; init; }

    /// <summary>
    /// A run for inputs that waited while the agent was paused: the fire writes no input of its
    /// own, and its run claims the waiting ones.
    /// </summary>
    public bool Resume { get; init; }

    /// <summary>
    /// The skill the trigger names; <see langword="null"/> lets the agent's rules or triage choose.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The run of another agent whose recorded inputs this fire replays; its run records it.
    /// </summary>
    public Guid? ReplayOfRunId { get; init; }

    /// <summary>
    /// The further recorded inputs of the replayed run, written after the fire's own input.
    /// </summary>
    public IReadOnlyList<NhAssistantReplayedInput> ReplayedInputs { get; init; } = [];
}

/// <summary>
/// One recorded input a replay writes again.
/// </summary>
internal sealed record NhAssistantReplayedInput(string? TriggerId, string Text, string? PayloadJson);

internal sealed class NhAssistantAgentTriggers(
    NhAssistantRegistrationState registration,
    NhAssistantAgentCatalog agents,
    NhAssistantSubjectAccessService subjects,
    NhAssistantThreadService threads,
    NhAssistantRunStore runs,
    NhAssistantAgentEvents events,
    IOptionsMonitor<NhAssistantOptions> assistantOptions,
    NhAssistantAdminStore admin,
    IServiceProvider services,
    ILogger<NhAssistantAgentTriggers> logger) : INhAssistantAgentTriggers
{
    /// <summary>
    /// The threads one resume starts runs for per batch; a resume continues with further batches.
    /// </summary>
    private const int ResumeBatchSize = 100;

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
        var matching = registration.Triggers.Values
            .Where(trigger => trigger.Source == NhAssistantTriggerSource.Event && trigger.EventType == typeof(TEvent))
            .ToArray();
        if (!assistantOptions.CurrentValue.Enabled)
        {
            // Off means off: no lambda runs and nothing is queued or audited.
            return matching
                .Select(trigger => new NhAssistantTriggerReceipt(
                    trigger.Id,
                    NhAssistantTriggerOutcomes.Rejected,
                    null,
                    null,
                    NhAssistantErrorCodes.Disabled))
                .ToArray();
        }

        await QueueRevalidationsAsync(evt, cancellationToken);
        var receipts = new List<NhAssistantTriggerReceipt>();
        var refusals = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var trigger in matching)
        {
            var live = await RefuseAgentAsync(trigger, trigger.AgentId, trigger.SkillId, refusals, cancellationToken);
            var variants = await AcceptingVariantsAsync(trigger, refusals, refusePaused: false, cancellationToken);
            if (live is not null && variants.Count == 0)
            {
                receipts.Add(live);
                continue;
            }
            if (trigger.When is { } when && !when(evt))
            {
                receipts.Add(live ?? Filtered(trigger));
                continue;
            }

            var fire = new NhAssistantTriggerFire(
                trigger.Id,
                trigger.AgentId,
                trigger.Subject(evt),
                trigger.Initiator(evt),
                trigger.Message(evt),
                trigger.Dedupe(evt),
                trigger.Conflict,
                trigger.Debounce)
            {
                PayloadJson = PayloadOf(trigger, evt),
                SkillId = trigger.SkillId
            };
            receipts.Add(live ?? await FireOrDeferAsync(fire, cancellationToken));
            foreach (var variant in variants)
            {
                // A variant gets the same event; publishers read the live agent's receipt.
                await FireOrDeferAsync(fire with { AgentId = variant, SkillId = SkillFor(trigger, variant) }, cancellationToken);
            }
        }

        return receipts;
    }

    /// <summary>
    /// The shadow variants of an agent (<c>ShadowOf</c>), which receive its triggers too.
    /// </summary>
    internal IReadOnlyList<string> VariantsOf(string agentId)
    {
        return registration.AgentRuns.Values
            .Where(options => string.Equals(options.ShadowOfAgentId, agentId, StringComparison.Ordinal))
            .Select(options => options.AgentId)
            .ToArray();
    }

    /// <summary>
    /// The skill a trigger names for <paramref name="agentId"/>: the trigger's skill when the agent
    /// declares it. A variant without that skill routes the run itself.
    /// </summary>
    internal string? SkillFor(NhAssistantTriggerDefinition trigger, string agentId)
    {
        return trigger.SkillId is { } skillId
               && registration.AgentRuns.TryGetValue(agentId, out var options)
               && options.Skills.ContainsKey(skillId)
            ? skillId
            : null;
    }

    /// <summary>
    /// The variants of the trigger's agent that take it now, each refusal audited once per agent in
    /// <paramref name="refusals"/>. With <paramref name="refusePaused"/> a paused variant takes none,
    /// as for schedule ticks.
    /// </summary>
    internal async Task<IReadOnlyList<string>> AcceptingVariantsAsync(
        NhAssistantTriggerDefinition trigger,
        Dictionary<string, string?>? refusals,
        bool refusePaused,
        CancellationToken cancellationToken)
    {
        var accepting = new List<string>();
        foreach (var variant in VariantsOf(trigger.AgentId))
        {
            if (await RefuseAgentAsync(trigger, variant, SkillFor(trigger, variant), refusals, cancellationToken, refusePaused) is null)
            {
                accepting.Add(variant);
            }
        }

        return accepting;
    }

    private static NhAssistantTriggerReceipt Filtered(NhAssistantTriggerDefinition trigger)
    {
        return new NhAssistantTriggerReceipt(
            trigger.Id,
            NhAssistantTriggerOutcomes.Ignored,
            null,
            null,
            NhAssistantTriggerReasons.Filtered);
    }

    /// <summary>
    /// Queues one revalidation per registration for <typeparamref name="TEvent"/> whose subject or
    /// objects have open agent proposals; most events find none and queue nothing. The operation
    /// joins the application's transaction like a run does. A proposal made between this check and
    /// the commit is not revalidated, but the decision and the execution still check it.
    /// </summary>
    private async Task QueueRevalidationsAsync<TEvent>(TEvent evt, CancellationToken cancellationToken)
        where TEvent : class
    {
        if (!registration.ProposalGuards.Any(guard => guard.Stages.HasFlag(NhAiProposalGuardStages.Revalidation)))
        {
            // Without a guard that revalidates, nothing could close.
            return;
        }

        foreach (var revalidation in registration.Revalidations.Where(item => item.EventType == typeof(TEvent)))
        {
            var subject = revalidation.Subject?.Invoke(evt);
            var keys = (revalidation.Keys?.Invoke(evt) ?? [])
                .Where(key => key is not null)
                .Distinct()
                .Take(NhAssistantLimits.MaxRevalidationKeys)
                .ToArray();
            var scopeKey = keys.Length == 0 ? null : revalidation.ScopeKey(evt);
            if (subject is not null && !await runs.HasOpenProposalsForSubjectAsync(subject, cancellationToken))
            {
                subject = null;
            }
            if (keys.Length > 0
                && !await runs.HasOpenProposalsByKeysAsync(
                    scopeKey ?? NhAiSubjectInfo.CreateScopeKey(null, null),
                    keys,
                    cancellationToken))
            {
                keys = [];
            }
            if (subject is null && keys.Length == 0)
            {
                continue;
            }

            var operations = services.GetService<INhBackgroundOperationService>();
            if (operations is null)
            {
                logger.LogWarning(
                    "Proposal revalidation for {EventType} needs background operations with AddAssistantAgentRuns.",
                    typeof(TEvent).Name);
                return;
            }

            var enqueued = await operations.EnqueueAsync(
                new NhAssistantRevalidateProposalsRequest(subject, keys, keys.Length == 0 ? null : scopeKey),
                new NhBackgroundOperationEnqueueOptions
                {
                    OwnerAgentKey = NhAssistantProposalInvalidation.SystemActorId,
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    DomainObjectType = subject?.Type,
                    DomainObjectId = subject?.Id
                },
                cancellationToken);
            if (!enqueued.Success)
            {
                logger.LogWarning(
                    "Proposal revalidation for {EventType} could not be queued.",
                    typeof(TEvent).Name);
            }
        }
    }

    public async Task<NhAssistantTriggerReceipt> StartAsync(
        string agentId,
        NhAiSubjectRef subject,
        string initiatorActorId,
        string? message,
        CancellationToken cancellationToken = default)
    {
        var fire = Manual(agentId, subject, initiatorActorId, message);
        if (await RefusalOfAsync(agentId, refusePaused: false, cancellationToken) is { } refusal)
        {
            if (refusal != NhAssistantErrorCodes.Disabled)
            {
                await events.TriggerAsync(agentId, null, fire.TriggerId, null, subject, initiatorActorId, refusal, accepted: false, cancellationToken);
            }

            return Rejected(fire, null, refusal);
        }

        return await FireOrDeferAsync(fire, cancellationToken);
    }

    /// <summary>
    /// Why the agent takes no triggers now, before any of a trigger's lambdas runs: the assistant
    /// is disabled, the agent is not configured, disabled in code or stopped, or, with
    /// <paramref name="refusePaused"/>, paused. <see langword="null"/> when it takes them.
    /// </summary>
    internal async Task<string?> RefusalOfAsync(string agentId, bool refusePaused, CancellationToken cancellationToken)
    {
        if (!assistantOptions.CurrentValue.Enabled)
        {
            return NhAssistantErrorCodes.Disabled;
        }
        if (!registration.AgentRuns.TryGetValue(agentId, out var runOptions))
        {
            return NhAssistantErrorCodes.AgentNotConfigured;
        }
        if (!runOptions.Enabled)
        {
            return NhAssistantErrorCodes.AgentConfiguredOff;
        }

        return await agents.FindRunStateAsync(agentId, cancellationToken) switch
        {
            null => NhAssistantErrorCodes.AgentNotFound,
            NhAssistantAgentRunStates.Stopped => NhAssistantErrorCodes.AgentStopped,
            NhAssistantAgentRunStates.Paused when refusePaused => NhAssistantErrorCodes.AgentPaused,
            _ => null
        };
    }

    /// <summary>
    /// The stored state of one skill of an agent: active without a row.
    /// </summary>
    internal async Task<string> SkillStateAsync(string agentId, string skillId, CancellationToken cancellationToken)
    {
        var states = await admin.GetSkillStatesAsync(agentId, cancellationToken);
        return states.TryGetValue(skillId, out var row) ? row.RunState : NhAssistantAgentRunStates.Active;
    }

    /// <summary>
    /// The receipt of a trigger that ends before its subject, message or payload lambdas run: its
    /// agent takes no triggers, audited once per agent in <paramref name="refusals"/>, or its
    /// <c>When</c> filter refuses the event, which is not audited. <see langword="null"/> when the
    /// trigger fires.
    /// </summary>
    internal async Task<NhAssistantTriggerReceipt?> RefuseEarlyAsync(
        NhAssistantTriggerDefinition trigger,
        object evt,
        Dictionary<string, string?>? refusals,
        CancellationToken cancellationToken)
    {
        return await RefuseAgentAsync(trigger, trigger.AgentId, trigger.SkillId, refusals, cancellationToken)
            ?? (trigger.When is { } when && !when(evt) ? Filtered(trigger) : null);
    }

    /// <summary>
    /// The receipt of a trigger that <paramref name="agentId"/> takes no event of now: the agent
    /// takes no triggers, audited once per agent in <paramref name="refusals"/>, or the skill the
    /// trigger names for it is stopped. <see langword="null"/> when the agent takes it.
    /// </summary>
    private async Task<NhAssistantTriggerReceipt?> RefuseAgentAsync(
        NhAssistantTriggerDefinition trigger,
        string agentId,
        string? skillId,
        Dictionary<string, string?>? refusals,
        CancellationToken cancellationToken,
        bool refusePaused = false)
    {
        string? refusal;
        if (refusals is null || !refusals.TryGetValue(agentId, out refusal))
        {
            refusal = await RefusalOfAsync(agentId, refusePaused, cancellationToken);
            if (refusals is not null)
            {
                refusals[agentId] = refusal;
            }
            if (refusal is not null && refusal != NhAssistantErrorCodes.Disabled)
            {
                await events.TriggerAsync(agentId, null, trigger.Id, null, null, null, refusal, accepted: false, cancellationToken);
            }
        }

        if (refusal is null && skillId is not null
            && await SkillStateAsync(agentId, skillId, cancellationToken) == NhAssistantAgentRunStates.Stopped)
        {
            // A stopped skill evaluates none of its triggers' lambdas either.
            refusal = NhAssistantErrorCodes.SkillStopped;
            await events.TriggerAsync(agentId, null, trigger.Id, null, null, null, refusal, accepted: false, cancellationToken, skillId);
        }
        if (refusal is null)
        {
            return null;
        }

        logger.LogInformation(
            "Trigger {TriggerId} for agent {AgentId} was rejected ({Reason}).",
            trigger.Id,
            agentId,
            refusal);
        return new NhAssistantTriggerReceipt(trigger.Id, NhAssistantTriggerOutcomes.Rejected, null, null, refusal);
    }

    /// <summary>
    /// Starts one run for every thread of the agent with inputs that waited while it was paused,
    /// with the trigger and initiator of the thread's oldest waiting input; the run claims them all.
    /// Threads whose subject is gone drop their inputs. Returns the runs it started.
    /// </summary>
    internal async Task<int> ResumeWaitingAsync(string agentId, CancellationToken cancellationToken, string? onlySkill = null)
    {
        // Events of skills that are still paused or stopped keep waiting.
        var held = onlySkill is null
            ? (await admin.GetSkillStatesAsync(agentId, cancellationToken)).Values
                .Where(row => row.RunState != NhAssistantAgentRunStates.Active)
                .Select(row => row.SkillId)
                .ToArray()
            : [];
        var started = 0;
        while (true)
        {
            var waiting = await runs.GetUnclaimedWaitingInputsAsync(agentId, ResumeBatchSize, cancellationToken, held, onlySkill);
            var progress = 0;
            foreach (var (thread, input) in waiting)
            {
                var receipt = await FireAsync(
                    new NhAssistantTriggerFire(
                        input.TriggerId ?? NhAssistantTriggerIds.Manual,
                        agentId,
                        new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                        input.InitiatorActorId ?? input.AuthorActorId,
                        input.Text,
                        input.EventKey,
                        NhAssistantTriggerConflict.Queue,
                        TimeSpan.Zero)
                    {
                        Resume = true,
                        SkillId = input.SkillId
                    },
                    cancellationToken);
                if (receipt.Outcome is NhAssistantTriggerOutcomes.Started or NhAssistantTriggerOutcomes.Queued)
                {
                    started++;
                    progress++;
                }
                else if (receipt.ReasonCode is NhAssistantErrorCodes.SubjectNotFound or NhAssistantErrorCodes.NoAccountableOwner)
                {
                    logger.LogWarning(
                        "The inputs that waited in thread {ConversationId} of agent {AgentId} were dropped ({Reason}).",
                        thread.ConversationId,
                        agentId,
                        receipt.ReasonCode);
                    await runs.DiscardWaitingInputsAsync(thread.ConversationId, cancellationToken);
                    progress++;
                }
            }

            if (waiting.Count < ResumeBatchSize || progress == 0)
            {
                return started;
            }
        }
    }

    /// <summary>
    /// Starts an agent for a person at once, for the assistant's own endpoints, whose callers need
    /// the run and its thread even when the application wraps the request in a transaction.
    /// </summary>
    internal Task<NhAssistantTriggerReceipt> StartNowAsync(
        string agentId,
        NhAiSubjectRef subject,
        string initiatorActorId,
        string? message,
        CancellationToken cancellationToken)
    {
        return FireAsync(Manual(agentId, subject, initiatorActorId, message), cancellationToken);
    }

    private static NhAssistantTriggerFire Manual(
        string agentId,
        NhAiSubjectRef subject,
        string initiatorActorId,
        string? message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(initiatorActorId);
        return new NhAssistantTriggerFire(
            NhAssistantTriggerIds.Manual,
            agentId,
            subject,
            initiatorActorId,
            string.IsNullOrWhiteSpace(message) ? "Start working on this subject." : message.Trim(),
            null,
            NhAssistantTriggerConflict.Queue,
            TimeSpan.Zero);
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
    /// Fires a scheduled trigger for one subject of one tick, for its agent or one of its variants.
    /// </summary>
    internal Task<NhAssistantTriggerReceipt> FireScheduledAsync(
        NhAssistantTriggerDefinition trigger,
        NhAssistantScheduledTick tick,
        CancellationToken cancellationToken,
        string? agentId = null)
    {
        var target = agentId ?? trigger.AgentId;
        return FireAsync(Create(trigger, tick, initiatorActorId: null) with { AgentId = target, SkillId = SkillFor(trigger, target) }, cancellationToken);
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

    /// <summary>
    /// Fires a verified webhook delivery for the variants of the trigger's agent that take it,
    /// whatever became of the live agent's fire.
    /// </summary>
    internal async Task MirrorWebhookAsync(
        NhAssistantTriggerDefinition trigger,
        NhAssistantWebhookDelivery delivery,
        CancellationToken cancellationToken)
    {
        var variants = await AcceptingVariantsAsync(trigger, null, refusePaused: false, cancellationToken);
        if (variants.Count == 0
            || (trigger.When is { } when && !when(delivery))
            || trigger.WebhookSubject?.Invoke(delivery.Body) is null)
        {
            return;
        }

        foreach (var variant in variants)
        {
            await FireAsync(
                Create(trigger, delivery, initiatorActorId: null) with { AgentId = variant, SkillId = SkillFor(trigger, variant) },
                cancellationToken);
        }
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
            trigger.Debounce)
        {
            SkillId = trigger.SkillId
        };
    }

    /// <summary>
    /// Fires a trigger the application published: inside the application's transaction it is
    /// deferred until the commit, otherwise it fires at once. The assistant's own endpoints, jobs
    /// and tools fire at once, because their callers need the run.
    /// </summary>
    private Task<NhAssistantTriggerReceipt> FireOrDeferAsync(
        NhAssistantTriggerFire fire,
        CancellationToken cancellationToken)
    {
        return InApplicationTransaction()
            ? DeferAsync(fire, cancellationToken)
            : FireAsync(fire, cancellationToken);
    }

    internal async Task<NhAssistantTriggerReceipt> FireAsync(
        NhAssistantTriggerFire fire,
        CancellationToken cancellationToken)
    {
        var receipt = await FireCoreAsync(fire, cancellationToken);
        var accepted = receipt.Outcome is NhAssistantTriggerOutcomes.Started
            or NhAssistantTriggerOutcomes.Queued
            or NhAssistantTriggerOutcomes.Coalesced
            or NhAssistantTriggerOutcomes.Superseded
            or NhAssistantTriggerOutcomes.Waiting;
        await events.TriggerAsync(
            fire.AgentId,
            receipt.ConversationId,
            fire.TriggerId,
            receipt.RunId,
            fire.Subject,
            fire.InitiatorActorId,
            accepted ? receipt.Outcome : receipt.ReasonCode ?? receipt.Outcome,
            accepted,
            cancellationToken,
            fire.SkillId);
        return receipt;
    }

    /// <summary>
    /// Fires a trigger that was published inside the application's transaction, now that the
    /// transaction committed. A retried operation finds the input it already wrote and stops.
    /// </summary>
    internal async Task<NhAssistantTriggerReceipt> FireDeferredAsync(
        NhAssistantFireTriggerRequest request,
        CancellationToken cancellationToken)
    {
        if (await runs.QueuedInputExistsAsync(request.InputId, cancellationToken))
        {
            return new NhAssistantTriggerReceipt(
                request.TriggerId,
                NhAssistantTriggerOutcomes.Deduplicated,
                null,
                null,
                NhAssistantTriggerReasons.Deduplicated);
        }

        return await FireAsync(
            new NhAssistantTriggerFire(
                request.TriggerId,
                request.AgentId,
                request.Subject,
                request.InitiatorActorId,
                request.Message,
                request.EventKey,
                request.Conflict,
                request.Debounce)
            {
                PayloadJson = request.PayloadJson,
                InputId = request.InputId,
                SkillId = registration.Triggers.TryGetValue(request.TriggerId, out var trigger) ? SkillFor(trigger, request.AgentId) : null
            },
            cancellationToken);
    }

    /// <summary>
    /// Fires the recorded inputs of <paramref name="sourceRun"/> into the shadow variant
    /// <paramref name="variantId"/> as one new run that records the run it replays, with the trigger,
    /// text and payload of each input and without an initiator. A retried replay finds the run it
    /// already started.
    /// </summary>
    internal Task<NhAssistantTriggerReceipt> ReplayAsync(
        string variantId,
        AssistantRun sourceRun,
        AssistantThread sourceThread,
        IReadOnlyList<AssistantQueuedInput> inputs,
        CancellationToken cancellationToken)
    {
        var first = inputs[0];
        var skillId = first.SkillId is { } skill
                      && registration.AgentRuns.TryGetValue(variantId, out var options)
                      && options.Skills.ContainsKey(skill)
            ? skill
            : null;
        return FireAsync(
            new NhAssistantTriggerFire(
                first.TriggerId ?? sourceRun.TriggerId,
                variantId,
                new NhAiSubjectRef(sourceThread.SubjectType, sourceThread.SubjectId),
                null,
                first.Text,
                "replay:" + sourceRun.Id.ToString("N"),
                NhAssistantTriggerConflict.Queue,
                TimeSpan.Zero)
            {
                PayloadJson = first.PayloadJson,
                SkillId = skillId,
                ReplayOfRunId = sourceRun.Id,
                ReplayedInputs = inputs
                    .Skip(1)
                    .Select(input => new NhAssistantReplayedInput(input.TriggerId, input.Text, input.PayloadJson))
                    .ToArray()
            },
            cancellationToken);
    }

    /// <summary>
    /// Queues a run again whose background operation never committed, for example because the
    /// process stopped between writing the run and enqueuing its operation. Returns
    /// <see langword="false"/> when its thread, agent or subject is gone or the agent is disabled.
    /// </summary>
    internal async Task<bool> RequeueOrphanAsync(AssistantRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var thread = await runs.FindThreadByConversationAsync(run.ConversationId, cancellationToken);
        if (thread is null
            || !registration.AgentRuns.TryGetValue(thread.AgentId, out var options)
            || !options.Enabled)
        {
            return false;
        }

        var agent = await agents.FindAsync(thread.AgentId, includeDisabled: false, cancellationToken);
        var subject = agent is null
            ? null
            : await subjects.DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), cancellationToken);
        if (agent is null || subject is null)
        {
            return false;
        }

        var enqueued = await EnqueueRunAsync(run.Id, thread, agent, subject, run.AccountableOwnerId, null, cancellationToken);
        if (!enqueued.Success)
        {
            return false;
        }

        await runs.SetRunAttemptAsync(run.Id, run.Attempt, enqueued.Data!.Id, cancellationToken);
        logger.LogWarning(
            "Run {RunId} of agent {AgentId} had no background operation and was queued again.",
            run.Id,
            thread.AgentId);
        return true;
    }

    /// <summary>
    /// Records a fire published inside the application's transaction as a background operation in
    /// that transaction. Nothing is written to the assistant's own tables before the commit.
    /// </summary>
    private async Task<NhAssistantTriggerReceipt> DeferAsync(
        NhAssistantTriggerFire fire,
        CancellationToken cancellationToken)
    {
        var inputId = Guid.NewGuid();
        var message = fire.Message.Length > registration.Limits.MaxMessageChars
            ? fire.Message[..registration.Limits.MaxMessageChars]
            : fire.Message;
        var enqueued = await Operations.EnqueueAsync(
            new NhAssistantFireTriggerRequest(
                inputId,
                fire.TriggerId,
                fire.AgentId,
                fire.Subject,
                fire.InitiatorActorId,
                message,
                fire.EventKey,
                fire.Conflict,
                fire.Debounce,
                fire.PayloadJson),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerAgentKey = NhAssistantProposalInvalidation.SystemActorId,
                IdempotencyKey = inputId.ToString("N"),
                // The fires of one agent and subject run one at a time, in the order of publishing.
                ConcurrencyKey = $"agent-trigger:{fire.AgentId}:{fire.Subject.Type}:{fire.Subject.Id}",
                ConflictBehavior = NhBackgroundOperationConflictBehavior.Queue,
                DomainObjectType = fire.Subject.Type,
                DomainObjectId = fire.Subject.Id
            },
            cancellationToken);
        if (!enqueued.Success)
        {
            logger.LogWarning(
                "Trigger {TriggerId} for agent {AgentId} could not be deferred until the commit.",
                fire.TriggerId,
                fire.AgentId);
            return Rejected(fire, null, NhAssistantRunStore.OrphanedRunCode);
        }

        return new NhAssistantTriggerReceipt(fire.TriggerId, NhAssistantTriggerOutcomes.Deferred, null, null, null);
    }

    /// <summary>
    /// Whether the caller works inside the application's transaction: the one on the context that
    /// holds the background operations, which a run's operation would join.
    /// </summary>
    private bool InApplicationTransaction()
    {
        var operations = services.GetService<IRepository<NhBackgroundOperation>>();
        return operations?.Context.Database.CurrentTransaction is not null;
    }

    private async Task<NhAssistantTriggerReceipt> FireCoreAsync(
        NhAssistantTriggerFire fire,
        CancellationToken cancellationToken)
    {
        if (!assistantOptions.CurrentValue.Enabled)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.Disabled);
        }
        if (!registration.AgentRuns.TryGetValue(fire.AgentId, out var options))
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentNotConfigured);
        }
        if (!options.Enabled)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentConfiguredOff);
        }

        var agent = await agents.FindAsync(fire.AgentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentNotFound);
        }
        if (agent.RunState == NhAssistantAgentRunStates.Stopped)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.AgentStopped);
        }

        var skillState = fire.SkillId is { } skillId ? await SkillStateAsync(fire.AgentId, skillId, cancellationToken) : null;
        if (skillState == NhAssistantAgentRunStates.Stopped)
        {
            return Rejected(fire, null, NhAssistantErrorCodes.SkillStopped);
        }

        // A paused agent or a paused skill lets the event wait.
        var paused = (agent.RunState == NhAssistantAgentRunStates.Paused || skillState == NhAssistantAgentRunStates.Paused)
            && !fire.Resume;

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
        if (!fire.Resume && fire.EventKey is { } eventKey)
        {
            if (await runs.FindRunByEventKeyAsync(conversationId, fire.TriggerId, eventKey, cancellationToken) is { } duplicate)
            {
                return new NhAssistantTriggerReceipt(
                    fire.TriggerId,
                    NhAssistantTriggerOutcomes.Deduplicated,
                    duplicate.Id,
                    conversationId,
                    NhAssistantTriggerReasons.Deduplicated);
            }
            if (await runs.HasWaitingInputAsync(conversationId, fire.TriggerId, eventKey, cancellationToken))
            {
                return new NhAssistantTriggerReceipt(
                    fire.TriggerId,
                    NhAssistantTriggerOutcomes.Deduplicated,
                    null,
                    conversationId,
                    NhAssistantTriggerReasons.Deduplicated);
            }
        }

        // Events join a queued run of the same skill only.
        var queued = await runs.FindQueuedRunAsync(conversationId, cancellationToken, fire.SkillId);
        var active = await runs.FindActiveRunAsync(conversationId, cancellationToken);
        if (paused)
        {
            if (fire.Conflict == NhAssistantTriggerConflict.Ignore && active is not null)
            {
                return new NhAssistantTriggerReceipt(
                    fire.TriggerId,
                    NhAssistantTriggerOutcomes.Ignored,
                    active.Id,
                    conversationId,
                    NhAssistantTriggerReasons.Busy);
            }

            // Rate limits count runs; the input waits for the resume, which starts one run per thread.
            await runs.AddQueuedInputAsync(CreateInput(fire, conversationId), cancellationToken);
            return new NhAssistantTriggerReceipt(
                fire.TriggerId,
                NhAssistantTriggerOutcomes.Waiting,
                null,
                conversationId,
                agent.RunState == NhAssistantAgentRunStates.Paused ? NhAssistantErrorCodes.AgentPaused : NhAssistantErrorCodes.SkillPaused);
        }
        if (fire.Resume && queued is not null)
        {
            // A queued run claims the waiting inputs when it starts.
            return new NhAssistantTriggerReceipt(fire.TriggerId, NhAssistantTriggerOutcomes.Coalesced, queued.Id, conversationId, null);
        }

        var since = DateTimeOffset.UtcNow.AddDays(-1);
        if (!fire.Resume
            && await runs.CountRunsSinceAsync(conversationId, since, cancellationToken) >= options.MaxRunsPerSubjectPerDay)
        {
            return Rejected(fire, conversationId, NhAssistantTriggerReasons.RateLimited);
        }

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
            SkillId = fire.SkillId,
            SkillChosenBy = fire.SkillId is null ? null : NhAssistantSkillChoosers.Trigger,
            ReplayOfRunId = fire.ReplayOfRunId,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await runs.AddRunAsync(run, cancellationToken);
        if (!fire.Resume)
        {
            await runs.AddQueuedInputAsync(input, cancellationToken);
            foreach (var replayed in fire.ReplayedInputs)
            {
                await runs.AddQueuedInputAsync(
                    CreateInput(
                        fire with
                        {
                            TriggerId = replayed.TriggerId ?? fire.TriggerId,
                            Message = replayed.Text,
                            PayloadJson = replayed.PayloadJson,
                            EventKey = null,
                            InputId = null
                        },
                        conversationId),
                    cancellationToken);
            }
        }

        var notBefore = fire.Conflict == NhAssistantTriggerConflict.Coalesce && fire.Debounce > TimeSpan.Zero
            ? DateTimeOffset.UtcNow.Add(fire.Debounce)
            : (DateTimeOffset?)null;
        var enqueued = await EnqueueRunAsync(run.Id, thread, agent, subject, accountable.Data!, notBefore, cancellationToken);
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
    /// Enqueues the background operation of a run, owned by the agent or its accountable user and
    /// serialized per agent and subject.
    /// </summary>
    private Task<TaskResult<NhBackgroundOperationViewModel>> EnqueueRunAsync(
        Guid runId,
        AssistantThread thread,
        NhAssistantAgent agent,
        NhAiSubjectInfo subject,
        string accountableOwnerId,
        DateTimeOffset? notBefore,
        CancellationToken cancellationToken)
    {
        var owner = OwnerActor(thread, accountableOwnerId, agent.Definition);
        return Operations.EnqueueAsync(
            new NhAssistantAgentRunRequest(runId),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = owner.UserId,
                OwnerAgentKey = owner.Key,
                DivisionId = subject.DivisionId,
                IdempotencyKey = runId.ToString("N"),
                ConcurrencyKey = $"agent:{agent.Id}:subject:{subject.ScopeKey}:{subject.Subject.Type}:{subject.Subject.Id}",
                ConflictBehavior = NhBackgroundOperationConflictBehavior.Queue,
                DomainObjectType = subject.Subject.Type,
                DomainObjectId = subject.Subject.Id,
                CorrelationId = runId.ToString("N"),
                NotBefore = notBefore
            },
            cancellationToken);
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
            Id = fire.InputId ?? Guid.NewGuid(),
            ConversationId = conversationId,
            Kind = personal ? NhAssistantQueuedInputKinds.Message : NhAssistantQueuedInputKinds.Event,
            AuthorActorId = personal ? fire.InitiatorActorId : null,
            TriggerId = fire.TriggerId,
            Text = text,
            PayloadJson = fire.PayloadJson,
            EventKey = fire.EventKey,
            InitiatorActorId = fire.InitiatorActorId,
            SkillId = fire.SkillId,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// The event's payload for rule handlers, as JSON within the stored limit.
    /// </summary>
    private static string? PayloadOf(NhAssistantTriggerDefinition trigger, object evt)
    {
        if (trigger.Payload?.Invoke(evt) is not { } payload)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(payload, payload.GetType(), JsonSerializerOptions.Web);
        if (json.Length > NhAssistantLimits.MaxInputPayloadCharacters)
        {
            throw new InvalidOperationException(
                $"The payload of trigger '{trigger.Id}' exceeds {NhAssistantLimits.MaxInputPayloadCharacters} characters of JSON.");
        }

        return json;
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
