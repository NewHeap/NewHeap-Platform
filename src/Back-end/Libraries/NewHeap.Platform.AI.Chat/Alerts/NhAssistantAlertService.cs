using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// Who raises an alert: an agent's rules in one run, or the application's code.
/// </summary>
/// <param name="AgentId">The agent; <see langword="null"/> for the application's code.</param>
/// <param name="RunId">The agent's run; <see langword="null"/> outside a run.</param>
/// <param name="Shadow">The agent or run works in shadow: its alerts are recorded and reach nobody.</param>
internal sealed record NhAssistantAlertRaiser(string? AgentId, Guid? RunId, bool Shadow)
{
    public static NhAssistantAlertRaiser Application { get; } = new(null, null, false);
}

/// <summary>
/// Raises and resolves alerts (FB87) for the application's code (<see cref="INhAssistantAlerts"/>) and
/// for rule runs (<see cref="ApplyRuleResultAsync"/>), and closes and purges them for the run
/// maintenance. Raising checks the kind and the subject, writes the title once in the current culture,
/// chooses the recipients with the kind's selectors or the application's <see cref="INhAssistantAlertPolicy"/>,
/// checks every one of them (<see cref="NhAssistantAlertRecipientCheck"/>), plans their deliveries
/// (<see cref="INhAssistantAlertScheduler"/>) and lets the store deduplicate. Shadow alerts get no
/// recipients. The audit is content-free. Every time is on the application's clock. Resolving by omission
/// and revalidation are in <c>NhAssistantAlertService.Lifecycle.cs</c>.
/// </summary>
internal sealed partial class NhAssistantAlertService(
    NhAssistantRegistrationState registration,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAlertRecipientCheck recipientCheck,
    INhAssistantAlertPolicy policy,
    INhAssistantAlertScheduler scheduler,
    NhAssistantAlertStore store,
    NhAssistantAlertTexts texts,
    NhAssistantAgentEvents events,
    NhAssistantAgentCatalog agents,
    ILogger<NhAssistantAlertService> logger,
    IServiceScopeFactory scopes,
    TimeProvider? timeProvider = null) : INhAssistantAlerts
{
    /// <summary>The most alerts one maintenance pass closes or purges per batch.</summary>
    internal const int MaintenanceBatchSize = 100;

    /// <summary>The most batches one maintenance pass works through, so a backlog spreads over passes.</summary>
    internal const int MaxMaintenanceBatches = 10;

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public Task<TaskResult<NhAssistantAlertReceipt>> RaiseAsync(NhAssistantAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return RaiseAsync(alert, NhAssistantAlertRaiser.Application, cancellationToken);
    }

    public Task<TaskResult<int>> ResolveAsync(NhAssistantAlertKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveAsync(key, NhAssistantAlertRaiser.Application, cancellationToken);
    }

    /// <summary>
    /// Raises and resolves the alerts of a rule result once the rules ended. Every kind must be one the
    /// agent lists (<c>Alerts(...)</c>); otherwise the result fails with
    /// <see cref="NhAssistantAlertCodes.KindNotListed"/> and nothing is raised or resolved, so the run
    /// fails with that code. A paused or stopped agent raises nothing, but still resolves. Each alert that
    /// is refused on its own, such as by a limit, is logged with its code and the run goes on.
    /// </summary>
    public async Task<TaskResult> ApplyRuleResultAsync(
        NhAssistantAgentRunOptions options,
        NhAssistantAlertRaiser raiser,
        IReadOnlyList<NhAssistantAlert> alerts,
        IReadOnlyList<NhAssistantAlertKey> resolves,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(raiser);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(resolves);
        if (alerts.Count == 0 && resolves.Count == 0)
        {
            return TaskResult.Succeeded();
        }

        var listed = options.AlertKinds.ToHashSet(StringComparer.Ordinal);
        var unlisted = alerts.Select(alert => alert.KindId)
            .Concat(resolves.Select(key => key.KindId))
            .FirstOrDefault(kindId => !listed.Contains(kindId) || !registration.AlertKinds.ContainsKey(kindId));
        if (unlisted is not null)
        {
            logger.LogWarning(
                "The rules of agent {AgentId} raised or resolved alert kind {KindId}, which the agent does not list with Alerts(...); none of its alerts were raised or resolved.",
                options.AgentId,
                unlisted);
            return TaskResult.Failed(NhAssistantAlertCodes.KindNotListed, "assistant.alert-kind-not-listed");
        }

        foreach (var key in resolves)
        {
            await ResolveAsync(key, raiser, cancellationToken);
        }

        if (alerts.Count == 0)
        {
            return TaskResult.Succeeded();
        }
        if (!await IsActiveAsync(options, cancellationToken))
        {
            logger.LogInformation(
                "Agent {AgentId} is paused or stopped, so its rules raised none of their {Count} alerts ({Code}).",
                options.AgentId,
                alerts.Count,
                NhAssistantAlertCodes.AgentNotActive);
            return TaskResult.Succeeded();
        }

        var limit = registration.Limits.MaxAlertsPerRun;
        for (var index = 0; index < alerts.Count; index++)
        {
            var alert = alerts[index];
            if (index >= limit)
            {
                logger.LogInformation(
                    "The rules of agent {AgentId} raised more than {Limit} alerts in one run; the remaining {Count} were refused ({Code}).",
                    options.AgentId,
                    limit,
                    alerts.Count - index,
                    NhAssistantAlertCodes.LimitReached);
                break;
            }

            var raised = await RaiseAsync(alert, raiser, cancellationToken);
            if (!raised.Success)
            {
                logger.LogInformation(
                    "An alert of kind {KindId} of agent {AgentId} was refused ({Code}).",
                    alert.KindId,
                    options.AgentId,
                    NhAssistantResults.CodeOf(raised, NhAssistantAlertCodes.KindUnknown));
            }
        }

        return TaskResult.Succeeded();
    }

    /// <summary>
    /// Raises one alert for <paramref name="raiser"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantAlertReceipt>> RaiseAsync(
        NhAssistantAlert alert,
        NhAssistantAlertRaiser raiser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(raiser);
        if (!registration.AlertKinds.TryGetValue(alert.KindId, out var kind))
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NhAssistantAlertCodes.KindUnknown, "assistant.alert-kind-unknown");
        }
        if (alert.Recipients.Count > 0 && !kind.Recipients.Includes(NhAssistantAlertRecipientSelector.Explicit))
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NhAssistantAlertCodes.RecipientsNotSelected, "assistant.alert-recipients-not-selected");
        }

        var now = _applicationClock.GetUtcNow();
        if (alert.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NhAssistantAlertCodes.Expired, "assistant.alert-expired");
        }

        var subject = await subjects.DescribeAsync(alert.Subject, cancellationToken);
        if (subject is null)
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NhAssistantAlertCodes.SubjectNotFound, "assistant.alert-subject-not-found");
        }

        // The title must be writable from the parameters; each reader's own culture is written later.
        if (texts.Render(kind, alert.Parameters, CultureInfo.CurrentUICulture) is null)
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NhAssistantAlertCodes.TextInvalid, "assistant.alert-text-invalid");
        }

        var urgency = alert.Urgency ?? kind.Urgency;
        var (planned, dropped) = raiser.Shadow
            ? ([], new Dictionary<string, string>(StringComparer.Ordinal))
            : await PlanRecipientsAsync(kind, subject, alert, urgency, raiser, now, cancellationToken);
        var write = await store.RaiseAsync(
            new NhAssistantAlertDraft(
                kind.Id,
                subject,
                alert.Key,
                raiser.Shadow,
                urgency,
                NhAssistantAlertTexts.Serialize(alert.Parameters),
                alert.ExpiresAt,
                raiser.AgentId,
                raiser.RunId,
                now)
            {
                Recipients = planned,
                Dropped = dropped,
                RepeatAfter = kind.RepeatAfter,
                MaxPerSubjectPerDay = registration.Limits.MaxAlertsPerSubjectPerDay,
                DayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)
            },
            cancellationToken);

        if (write.ExpiredFirst is { } expired)
        {
            await events.AlertResolvedAsync(expired, NhAssistantAlertStatuses.Expired, cancellationToken);
        }
        if (write.Alert is not { } stored)
        {
            return TaskResult<NhAssistantAlertReceipt>.Failed(NhAssistantAlertCodes.LimitReached, "assistant.alert-limit-reached");
        }

        foreach (var (actorId, reason) in dropped)
        {
            await events.AlertRecipientDroppedAsync(stored, actorId, reason, cancellationToken);
        }
        if (write.Outcome != NhAssistantAlertOutcome.Unchanged)
        {
            var code = write.Outcome switch
            {
                NhAssistantAlertOutcome.Raised => NhAssistantAlertCodes.Raised,
                NhAssistantAlertOutcome.Redelivered => NhAssistantAlertCodes.Redelivered,
                _ => NhAssistantAlertCodes.Updated
            };
            await events.AlertRaisedAsync(stored, code, write.RecipientCount, cancellationToken);
        }

        return TaskResult<NhAssistantAlertReceipt>.Succeeded(
            new NhAssistantAlertReceipt(stored.Id, write.Outcome, stored.Revision, stored.Shadow, write.RecipientCount));
    }

    /// <summary>
    /// Resolves the open alerts <paramref name="key"/> names for <paramref name="raiser"/>: live alerts, or
    /// for a shadow raiser its shadow alerts only.
    /// </summary>
    public async Task<TaskResult<int>> ResolveAsync(
        NhAssistantAlertKey key,
        NhAssistantAlertRaiser raiser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(raiser);
        if (!registration.AlertKinds.ContainsKey(key.KindId))
        {
            return TaskResult<int>.Failed(NhAssistantAlertCodes.KindUnknown, "assistant.alert-kind-unknown");
        }

        // A subject its resolver still describes is resolved in its own scope; a deleted one in any.
        var subject = await subjects.DescribeAsync(key.Subject, cancellationToken);
        var closed = await store.ResolveAsync(
            key.KindId,
            key.Subject,
            key.Key,
            subject?.ScopeKey,
            raiser.Shadow,
            _applicationClock.GetUtcNow(),
            cancellationToken);
        foreach (var alert in closed)
        {
            await events.AlertResolvedAsync(alert, NhAssistantAlertStatuses.Resolved, cancellationToken);
        }

        return TaskResult<int>.Succeeded(closed.Count);
    }

    /// <summary>
    /// The run maintenance's part: closes open alerts whose expiry passed, then purges closed alerts past
    /// <see cref="NhAssistantLimits.AlertRetention"/>, in bounded batches. Returns how many it closed and
    /// purged.
    /// </summary>
    public async Task<(int Expired, int Purged)> MaintainAsync(CancellationToken cancellationToken)
    {
        var now = _applicationClock.GetUtcNow();
        var expiredCount = 0;
        for (var batch = 0; batch < MaxMaintenanceBatches; batch++)
        {
            var expired = await store.ExpireDueAsync(now, MaintenanceBatchSize, cancellationToken);
            foreach (var alert in expired)
            {
                await events.AlertResolvedAsync(alert, NhAssistantAlertStatuses.Expired, cancellationToken);
            }

            expiredCount += expired.Count;
            if (expired.Count < MaintenanceBatchSize)
            {
                break;
            }
        }

        var purgedCount = 0;
        var before = now - registration.Limits.AlertRetention;
        for (var batch = 0; batch < MaxMaintenanceBatches; batch++)
        {
            var purged = await store.PurgeClosedAsync(before, MaintenanceBatchSize, cancellationToken);
            purgedCount += purged;
            if (purged < MaintenanceBatchSize)
            {
                break;
            }
        }

        if (expiredCount > 0 || purgedCount > 0)
        {
            logger.LogInformation(
                "Assistant maintenance closed {Expired} expired alerts and purged {Purged} closed alerts.",
                expiredCount,
                purgedCount);
        }

        return (expiredCount, purgedCount);
    }

    /// <summary>
    /// The people the alert reaches now and those who were chosen but no longer qualify, with why. The
    /// kind's selectors choose, unless the application's policy does; at most
    /// <see cref="NhAssistantAlertLimits.MaxRecipients"/> people are planned.
    /// </summary>
    private async Task<(IReadOnlyList<NhAssistantAlertPlannedRecipient> Planned, Dictionary<string, string> Dropped)> PlanRecipientsAsync(
        NhAssistantAlertKind kind,
        NhAiSubjectInfo subject,
        NhAssistantAlert alert,
        NhAssistantAlertUrgency urgency,
        NhAssistantAlertRaiser raiser,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var approvers = new List<NhAiSubjectMember>();
        var members = new List<NhAiSubjectMember>();
        var memberRole = kind.Recipients.MinimumMemberRole;
        if (kind.Recipients.Includes(NhAssistantAlertRecipientSelector.SubjectApprovers)
            || memberRole is not null
            || policy is not NhAssistantDefaultAlertPolicy)
        {
            await foreach (var member in subjects.ListMembersAsync(subject.Subject, cancellationToken))
            {
                if (member.Role >= NhAiSubjectRole.Approver)
                {
                    approvers.Add(member);
                }
                if (memberRole is { } minimum && member.Role >= minimum)
                {
                    members.Add(member);
                }
            }
        }

        var selected = new List<string>();
        foreach (var selector in kind.Recipients.Selectors)
        {
            var chosen = selector switch
            {
                NhAssistantAlertRecipientSelector.SubjectResponsible => subject.ResponsibleActorId is { } responsible
                    ? [responsible]
                    : [],
                NhAssistantAlertRecipientSelector.Explicit => alert.Recipients,
                NhAssistantAlertRecipientSelector.SubjectMembers => members.Select(member => member.ActorId).ToArray(),
                _ => approvers.Select(member => member.ActorId).ToArray()
            };
            selected.AddRange(chosen.Where(actorId => !string.IsNullOrWhiteSpace(actorId)));
        }

        var context = new NhAssistantAlertRecipientContext(kind, subject, alert.Key, urgency, alert.Recipients, raiser.AgentId)
        {
            Approvers = approvers,
            Selected = selected.Distinct(StringComparer.Ordinal).ToArray()
        };
        var recipients = await policy.SelectRecipientsAsync(context, cancellationToken);

        var planned = new List<NhAssistantAlertPlannedRecipient>();
        var dropped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var actorId in (recipients ?? []).Where(actorId => actorId is not null).Distinct(StringComparer.Ordinal))
        {
            if (planned.Count >= NhAssistantAlertLimits.MaxRecipients)
            {
                dropped.TryAdd(actorId, NhAssistantAlertDropReasons.RecipientLimit);
                continue;
            }

            var verdict = await recipientCheck.CheckAsync(actorId, subject, kind, cancellationToken);
            if (!verdict.Qualifies)
            {
                dropped.TryAdd(actorId, verdict.DropReason!);
                continue;
            }

            var dueAt = await scheduler.DueAtAsync(new NhAssistantAlertScheduleRequest(kind, urgency, actorId, now), cancellationToken);
            planned.Add(new NhAssistantAlertPlannedRecipient(actorId, dueAt < now ? now : dueAt));
        }

        return (planned, dropped);
    }

    /// <summary>
    /// Whether the agent raises alerts now: neither paused nor stopped, by an administrator or in code.
    /// </summary>
    private async Task<bool> IsActiveAsync(NhAssistantAgentRunOptions options, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return false;
        }

        var state = await agents.FindRunStateAsync(options.AgentId, cancellationToken);
        return state is not (NhAssistantAgentRunStates.Paused or NhAssistantAgentRunStates.Stopped);
    }
}
