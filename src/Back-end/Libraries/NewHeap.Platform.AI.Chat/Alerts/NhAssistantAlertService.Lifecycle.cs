using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// The lifecycle of open alerts (FB87 phase 2): resolving by omission when rules finish a run
/// (<see cref="NhAssistantAlertKind.ResolveWhenNotRaised"/>) and the application's explicit revalidation
/// of a subject's alerts with the checks their kinds declare (FB87-10). Both resolve as resolving by key
/// does, with the audit <see cref="NhAssistantAuditEventKind.AgentAlertResolved"/> and the reason in its
/// result code.
/// </summary>
internal sealed partial class NhAssistantAlertService
{
    /// <summary>
    /// Whether an agent's rule runs resolve by omission: it lists a kind that asks for it.
    /// </summary>
    public static bool ResolvesWhenNotRaised(NhAssistantAgentRunOptions options, NhAssistantRegistrationState registration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registration);
        return options.AlertKinds.Any(kindId => registration.AlertKinds.TryGetValue(kindId, out var kind) && kind.ResolveWhenNotRaised);
    }

    /// <summary>
    /// Resolves by omission once the rules finished their run on <paramref name="subject"/> with a result
    /// that raised <paramref name="raised"/>: for each kind the agent lists that asks for it, the agent's
    /// open alerts of the kind about that subject whose key the result did not raise, live or shadow as
    /// the run is. An alert the result raised counts even when it was refused, such as by a limit, so a
    /// refusal never resolves what applies. A run on the whole application (<c>nh-system</c>) resolves only
    /// explicitly. The caller calls it only for rules that finished: never after a failure, a hand-over or
    /// a cancellation. Returns how many alerts it resolved.
    /// </summary>
    public async Task<int> ResolveNotRaisedAsync(
        NhAssistantAgentRunOptions options,
        NhAssistantAlertRaiser raiser,
        NhAiSubjectInfo subject,
        IReadOnlyList<NhAssistantAlert> raised,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(raiser);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(raised);
        if (raiser.AgentId is not { } agentId || NhAiSystemSubjects.IsSystem(subject.Subject))
        {
            return 0;
        }

        var resolved = 0;
        foreach (var kindId in options.AlertKinds)
        {
            if (!registration.AlertKinds.TryGetValue(kindId, out var kind) || !kind.ResolveWhenNotRaised)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var keys = raised
                .Where(alert => alert.KindId == kindId && alert.Subject == subject.Subject)
                .Select(alert => alert.Key)
                .ToHashSet(StringComparer.Ordinal);
            var closed = await store.ResolveNotRaisedAsync(
                kindId,
                subject.Subject,
                subject.ScopeKey,
                raiser.Shadow,
                agentId,
                keys,
                _applicationClock.GetUtcNow(),
                NhAssistantAlertLimits.MaxRevalidatedAlerts,
                cancellationToken);
            foreach (var alert in closed)
            {
                await events.AlertResolvedAsync(alert, NhAssistantAlertCodes.NotRaised, cancellationToken);
            }

            resolved += closed.Count;
        }

        if (resolved > 0)
        {
            logger.LogInformation(
                "The rules of agent {AgentId} did not raise {Count} open alerts again, so they were resolved ({Code}).",
                agentId,
                resolved,
                NhAssistantAlertCodes.NotRaised);
        }

        return resolved;
    }

    public async Task<TaskResult<NhAssistantAlertRevalidationResult>> RevalidateAsync(
        NhAiSubjectRef subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var checkedKinds = registration.AlertKinds.Values
            .Where(kind => kind.CheckType is not null)
            .ToDictionary(kind => kind.Id, StringComparer.Ordinal);
        if (checkedKinds.Count == 0)
        {
            return TaskResult<NhAssistantAlertRevalidationResult>.Succeeded(NhAssistantAlertRevalidationResult.Empty);
        }

        // A subject its resolver still describes is revalidated in its own scope; a deleted one in any.
        var described = await subjects.DescribeAsync(subject, cancellationToken);
        var open = await store.ListOpenToRevalidateAsync(
            subject,
            described?.ScopeKey,
            checkedKinds.Keys,
            _applicationClock.GetUtcNow(),
            NhAssistantAlertLimits.MaxRevalidatedAlerts + 1,
            cancellationToken);
        var hasMore = open.Count > NhAssistantAlertLimits.MaxRevalidatedAlerts;

        var resolved = new List<NhAssistantResolvedAlert>();
        var checkedCount = 0;
        var failed = 0;
        foreach (var alert in open.Take(NhAssistantAlertLimits.MaxRevalidatedAlerts))
        {
            checkedCount++;
            var validity = await CheckAsync(checkedKinds[alert.KindId], alert, cancellationToken);
            if (validity is null)
            {
                failed++;
                continue;
            }
            if (validity != NhAssistantAlertValidity.Resolve)
            {
                continue;
            }

            // Only as the check saw it: an alert raised again meanwhile carries content it did not check.
            var closed = await store.ResolveUnchangedAsync(alert, _applicationClock.GetUtcNow(), cancellationToken);
            if (closed is null)
            {
                continue;
            }

            await events.AlertResolvedAsync(closed, NhAssistantAlertCodes.Revalidated, cancellationToken);
            resolved.Add(new NhAssistantResolvedAlert(closed.Id, closed.KindId, closed.Key, closed.Shadow));
        }

        if (resolved.Count > 0 || failed > 0)
        {
            logger.LogInformation(
                "Revalidating the alerts of a {SubjectType} checked {Checked}, resolved {Resolved} and failed to check {Failed}.",
                subject.Type,
                checkedCount,
                resolved.Count,
                failed);
        }

        return TaskResult<NhAssistantAlertRevalidationResult>.Succeeded(
            new NhAssistantAlertRevalidationResult(resolved, checkedCount, failed, hasMore));
    }

    /// <summary>
    /// Asks the kind's check about one open alert, in a service scope of its own, so a check that breaks
    /// its scope never breaks the next. Returns <see langword="null"/> when it failed, which is logged
    /// with the exception's type only, since application code can carry content in its messages.
    /// </summary>
    private async Task<NhAssistantAlertValidity?> CheckAsync(
        NhAssistantAlertKind kind,
        AssistantAlert alert,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var check = (INhAssistantAlertCheck)scope.ServiceProvider.GetRequiredService(kind.CheckType!);
            var context = new NhAssistantAlertCheckContext(
                alert.Id,
                alert.KindId,
                new NhAiSubjectRef(alert.SubjectType, alert.SubjectId),
                alert.Key,
                NhAssistantAlertTexts.ParametersOf(alert.ParametersJson),
                alert.RaisedAt,
                alert.Shadow);
            return await check.CheckAsync(context, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(
                "Alert check {CheckType} of kind {KindId} failed to check alert {AlertId} with {ExceptionType}; the alert stays open.",
                kind.CheckType!.FullName,
                kind.Id,
                alert.Id,
                exception.GetType().Name);
            return null;
        }
    }
}
