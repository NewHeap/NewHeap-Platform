using System.Globalization;
using NewHeap.Platform.AI.Chat.Alerts;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// What a person's alert list asks for, read from the request: the states, kinds, subject and urgencies.
/// </summary>
internal sealed record NhAssistantAlertQuery(
    string? Status,
    IReadOnlySet<NhAssistantAlertReadingState> States,
    IReadOnlyList<string> KindIds,
    string? SubjectType,
    string? SubjectId,
    IReadOnlyList<NhAssistantAlertUrgency> Urgencies)
{
    /// <summary>The most kinds one filter names.</summary>
    public const int MaxKinds = 20;

    /// <summary>
    /// Reads the filter of <c>GET alerts</c> and <c>GET alerts/summary</c>; a malformed one fails with
    /// <see cref="NhAssistantAlertCodes.FilterInvalid"/>.
    /// </summary>
    public static TaskResult<NhAssistantAlertQuery> Read(
        string? status,
        string[]? kind,
        string? subjectType,
        string? subjectId,
        string[]? urgency)
    {
        var kinds = kind?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var urgencies = new List<NhAssistantAlertUrgency>();
        foreach (var name in urgency?.Distinct(StringComparer.Ordinal) ?? [])
        {
            if (!NhAssistantAlertUrgencyNames.TryParse(name, out var parsed))
            {
                return Invalid();
            }

            urgencies.Add(parsed);
        }

        var valid = (string.IsNullOrEmpty(status) || NhAssistantAlertListStatuses.IsValid(status))
            && kinds.Length <= MaxKinds
            && kinds.All(item => !string.IsNullOrWhiteSpace(item) && item.Length <= NhAssistantAlertLimits.MaxKindIdLength)
            && (subjectType is null || NhAiSubjectRef.IsValidType(subjectType))
            && (subjectId is null
                || (subjectType is not null && subjectId.Length is > 0 and <= NhAiSubjectRef.MaxIdLength && subjectId.Trim() == subjectId));
        if (!valid)
        {
            return Invalid();
        }

        var listed = string.IsNullOrEmpty(status) ? null : status;
        return TaskResult<NhAssistantAlertQuery>.Succeeded(
            new NhAssistantAlertQuery(listed, StatesOf(listed), kinds, subjectType, subjectId, urgencies));
    }

    /// <summary>
    /// The states a status selects: one, two for <c>open</c> and <c>closed</c>, and every state but a
    /// dismissal without a status.
    /// </summary>
    private static IReadOnlySet<NhAssistantAlertReadingState> StatesOf(string? status)
    {
        return status switch
        {
            NhAssistantAlertListStatuses.Unread => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Unread },
            NhAssistantAlertListStatuses.Read => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Read },
            NhAssistantAlertListStatuses.Resolved => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Resolved },
            NhAssistantAlertListStatuses.Expired => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Expired },
            NhAssistantAlertListStatuses.Dismissed => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Dismissed },
            NhAssistantAlertListStatuses.Open => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Unread, NhAssistantAlertReadingState.Read },
            NhAssistantAlertListStatuses.Closed => new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Resolved, NhAssistantAlertReadingState.Expired },
            _ => new HashSet<NhAssistantAlertReadingState>
            {
                NhAssistantAlertReadingState.Unread,
                NhAssistantAlertReadingState.Read,
                NhAssistantAlertReadingState.Resolved,
                NhAssistantAlertReadingState.Expired
            }
        };
    }

    private static TaskResult<NhAssistantAlertQuery> Invalid()
    {
        return TaskResult<NhAssistantAlertQuery>.Failed(NhAssistantAlertCodes.FilterInvalid, "The alert filter is invalid.");
    }
}

/// <summary>
/// A person's side of alerts (FB87): their own list, the counts of a badge, and marking one alert read
/// or dismissing it. Only the caller's own deliveries are read, and every alert passes the recipient
/// check (<see cref="NhAssistantAlertRecipientCheck"/>) with the caller's tenant before anything of it
/// is shown or changed, so a person who lost access to a subject no longer reads its alerts' stored
/// text: such alerts are left out of the list and the counts, and their id answers like an unknown one.
/// Alerts of a kind the host no longer registers are left out too. A host without alert kinds reads no
/// store at all. Titles and texts are written in the request's language.
/// </summary>
internal sealed class NhAssistantAlertList(
    NhAssistantRegistrationState registration,
    NhAssistantAlertStore store,
    NhAssistantAlertRecipientCheck recipientCheck,
    NhAssistantAlertTexts texts,
    NhAssistantSubjectAccessService subjects,
    NhAssistantAgentEvents events,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// One page of the caller's alerts that <paramref name="query"/> selects, the most recently raised
    /// first, or per parent subject with <paramref name="grouped"/>.
    /// </summary>
    public async Task<NhAssistantAlertListDto> ListAsync(
        NhAiInvocationContext caller,
        NhAssistantAlertQuery query,
        int page,
        int itemsPerPage,
        bool grouped,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);
        var now = _applicationClock.GetUtcNow();
        var visible = await VisibleAsync(caller, query, query.States, now, withoutMuted: false, cancellationToken);

        var ordered = grouped ? GroupedOrder(visible) : visible;
        var start = (page - 1) * itemsPerPage;
        var items = ordered.Skip(start).Take(itemsPerPage).ToArray();
        var culture = CultureInfo.CurrentUICulture;
        return new NhAssistantAlertListDto(
            items.Select(item => ToDto(item.Delivery, item.Kind, now, culture)).ToArray(),
            ordered.Count)
        {
            Status = query.Status,
            Groups = grouped ? GroupsOf(ordered, start, items.Length) : null
        };
    }

    /// <summary>
    /// The counts of a badge: the caller's unread open alerts that <paramref name="query"/>'s kinds and
    /// subject select, in total and per urgency, with the list's visibility.
    /// </summary>
    public async Task<NhAssistantAlertSummaryDto> SummarizeAsync(
        NhAiInvocationContext caller,
        NhAssistantAlertQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(query);
        var unread = new HashSet<NhAssistantAlertReadingState> { NhAssistantAlertReadingState.Unread };
        // A kind the person muted is listed, but never asks for their attention.
        var visible = await VisibleAsync(caller, query, unread, _applicationClock.GetUtcNow(), withoutMuted: true, cancellationToken);
        var counts = visible
            .GroupBy(item => item.Delivery.Alert!.Urgency)
            .ToDictionary(group => group.Key, group => group.Count());
        return new NhAssistantAlertSummaryDto(
            visible.Count,
            new NhAssistantAlertUrgencyCountsDto(
                counts.GetValueOrDefault(NhAssistantAlertUrgency.Info),
                counts.GetValueOrDefault(NhAssistantAlertUrgency.Warning),
                counts.GetValueOrDefault(NhAssistantAlertUrgency.Urgent)));
    }

    /// <summary>
    /// Marks the caller's alert read: a delivery that still waits is then not sent. Reading it again, or
    /// reading an alert the caller dismissed or that closed before it went out, changes nothing. Returns
    /// the alert as the caller's list now shows it, or <see cref="NhAssistantAlertCodes.NotFound"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantAlertDto>> MarkReadAsync(
        NhAiInvocationContext caller,
        Guid alertId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var found = await FindAsync(caller, alertId, cancellationToken);
        if (found is null)
        {
            return NotFound();
        }

        await store.MarkReadFromListAsync(alertId, caller.ActorId, _applicationClock.GetUtcNow(), cancellationToken);
        return await CurrentAsync(caller.ActorId, alertId, found.Value.Kind, cancellationToken);
    }

    /// <summary>
    /// Dismisses the caller's alert: it leaves their list, and raising it again does not reach them until
    /// its key changes. The first dismissal is audited with <see cref="NhAssistantAuditEventKind.AgentAlertDismissed"/>,
    /// content-free and with the user who impersonates the caller, if any; dismissing it again changes
    /// nothing. Returns the alert as the caller's list now shows it, or <see cref="NhAssistantAlertCodes.NotFound"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantAlertDto>> DismissAsync(
        NhAiInvocationContext caller,
        Guid alertId,
        string? impersonationOriginActorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var found = await FindAsync(caller, alertId, cancellationToken);
        if (found is null)
        {
            return NotFound();
        }

        if (await store.DismissOnceAsync(alertId, caller.ActorId, _applicationClock.GetUtcNow(), cancellationToken))
        {
            await events.AlertDismissedAsync(found.Value.Delivery.Alert!, caller.ActorId, cancellationToken, impersonationOriginActorId);
        }

        return await CurrentAsync(caller.ActorId, alertId, found.Value.Kind, cancellationToken);
    }

    /// <summary>
    /// The caller's deliveries in <paramref name="states"/> that the query selects and the caller may read
    /// now, in the store's order, at most <see cref="NhAssistantAlertListDto.MaxListed"/>, without those
    /// withheld for a muted kind when <paramref name="withoutMuted"/>. The recipient check runs once per
    /// subject.
    /// </summary>
    private async Task<IReadOnlyList<(AssistantAlertDelivery Delivery, NhAssistantAlertKind Kind)>> VisibleAsync(
        NhAiInvocationContext caller,
        NhAssistantAlertQuery query,
        IReadOnlySet<NhAssistantAlertReadingState> states,
        DateTimeOffset now,
        bool withoutMuted,
        CancellationToken cancellationToken)
    {
        if (registration.AlertKinds.Count == 0)
        {
            // A host without alert kinds has nothing to list and may not have the alert tables.
            return [];
        }

        var filter = new NhAssistantAlertListFilter(states, now)
        {
            KindIds = query.KindIds,
            SubjectType = query.SubjectType,
            SubjectId = query.SubjectId,
            Urgencies = query.Urgencies,
            WithoutMuted = withoutMuted
        };
        var deliveries = await store.ListForRecipientAsync(caller.ActorId, filter, NhAssistantAlertListDto.MaxListed, cancellationToken);
        var verdicts = new Dictionary<(string Type, string Id), bool>();
        var visible = new List<(AssistantAlertDelivery, NhAssistantAlertKind)>(deliveries.Count);
        foreach (var delivery in deliveries)
        {
            var alert = delivery.Alert!;
            if (!registration.AlertKinds.TryGetValue(alert.KindId, out var kind))
            {
                continue;
            }

            var subject = (alert.SubjectType, alert.SubjectId);
            if (!verdicts.TryGetValue(subject, out var qualifies))
            {
                var verdict = await recipientCheck.CheckAsync(caller.ActorId, alert, cancellationToken, inRequest: true, caller.TenantId);
                qualifies = verdict.Qualifies;
                verdicts[subject] = qualifies;
            }
            if (qualifies)
            {
                visible.Add((delivery, kind));
            }
        }

        return visible;
    }

    /// <summary>
    /// The caller's delivery of one alert with its kind, when the caller has one that was not dropped,
    /// its kind is registered and the caller may read its subject now; otherwise <see langword="null"/>.
    /// </summary>
    private async Task<(AssistantAlertDelivery Delivery, NhAssistantAlertKind Kind)?> FindAsync(
        NhAiInvocationContext caller,
        Guid alertId,
        CancellationToken cancellationToken)
    {
        if (registration.AlertKinds.Count == 0)
        {
            return null;
        }

        var delivery = await store.FindDeliveryAsync(alertId, caller.ActorId, cancellationToken);
        if (delivery?.Alert is not { } alert
            || delivery.Status == NhAssistantAlertDeliveryStatuses.Dropped
            || alert.Shadow
            || !registration.AlertKinds.TryGetValue(alert.KindId, out var kind))
        {
            return null;
        }

        var verdict = await recipientCheck.CheckAsync(caller.ActorId, alert, cancellationToken, inRequest: true, caller.TenantId);
        return verdict.Qualifies ? (delivery, kind) : null;
    }

    private async Task<TaskResult<NhAssistantAlertDto>> CurrentAsync(
        string actorId,
        Guid alertId,
        NhAssistantAlertKind kind,
        CancellationToken cancellationToken)
    {
        var delivery = await store.FindDeliveryAsync(alertId, actorId, cancellationToken);
        if (delivery?.Alert is null)
        {
            // Purged or forgotten in between.
            return NotFound();
        }

        return TaskResult<NhAssistantAlertDto>.Succeeded(
            ToDto(delivery, kind, _applicationClock.GetUtcNow(), CultureInfo.CurrentUICulture));
    }

    private NhAssistantAlertDto ToDto(AssistantAlertDelivery delivery, NhAssistantAlertKind kind, DateTimeOffset now, CultureInfo culture)
    {
        var alert = delivery.Alert!;
        var state = NhAssistantAlertStore.StateOf(delivery, now);
        var written = texts.Render(kind, alert.ParametersJson, culture);
        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, alert.SubjectType, alert.SubjectId);
        var read = delivery.Status == NhAssistantAlertDeliveryStatuses.Read;
        var withheld = delivery.Status == NhAssistantAlertDeliveryStatuses.Withheld;
        var dismissed = state == NhAssistantAlertReadingState.Dismissed;
        return new NhAssistantAlertDto(
            alert.Id,
            delivery.Id,
            alert.KindId,
            kind.DisplayNameKey,
            NhAssistantAlertUrgencyNames.Of(alert.Urgency),
            written?.Title,
            written?.Text,
            alert.SubjectType,
            alert.SubjectId,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, alert.SubjectType, alert.SubjectId, alert.SubjectDisplayName),
            StatusOf(state),
            alert.RaisedAt,
            alert.UpdatedAt)
        {
            SubjectTypeName = typeName,
            SubjectLink = link,
            Parent = ParentOf(alert),
            Revision = alert.Revision,
            ExpiresAt = alert.ExpiresAt,
            ClosedAt = alert.ClosedAt,
            IsRead = read,
            ReadAt = read ? delivery.ReadAt : null,
            IsDismissed = dismissed,
            DismissedAt = dismissed ? delivery.DismissedAt : null,
            IsResolved = alert.Status == NhAssistantAlertStatuses.Resolved,
            IsExpired = alert.Status == NhAssistantAlertStatuses.Expired
                || (alert.Status == NhAssistantAlertStatuses.Open && alert.ExpiresAt is { } expiresAt && expiresAt <= now),
            IsWithheld = withheld,
            WithheldReason = withheld ? delivery.ReasonCode : null,
            RaisedByModel = alert.RaisedByModel
        };
    }

    private NhAssistantAlertParentDto? ParentOf(AssistantAlert alert)
    {
        if (alert.ParentSubjectType is not { } type || alert.ParentSubjectId is not { } id)
        {
            return null;
        }

        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, type, id);
        return new NhAssistantAlertParentDto(type, id, NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, type, id, alert.ParentDisplayName))
        {
            SubjectTypeName = typeName,
            SubjectLink = link
        };
    }

    private static string StatusOf(NhAssistantAlertReadingState state)
    {
        return state switch
        {
            NhAssistantAlertReadingState.Unread => NhAssistantAlertListStatuses.Unread,
            NhAssistantAlertReadingState.Read => NhAssistantAlertListStatuses.Read,
            NhAssistantAlertReadingState.Resolved => NhAssistantAlertListStatuses.Resolved,
            NhAssistantAlertReadingState.Expired => NhAssistantAlertListStatuses.Expired,
            NhAssistantAlertReadingState.Dismissed => NhAssistantAlertListStatuses.Dismissed,
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    /// <summary>
    /// The group of an alert: the parent its subject belongs to, or its own subject without one.
    /// </summary>
    private static (string Type, string Id) GroupOf(AssistantAlert alert)
    {
        return alert.ParentSubjectType is { } type && alert.ParentSubjectId is { } id
            ? (type, id)
            : (alert.SubjectType, alert.SubjectId);
    }

    /// <summary>
    /// The list per group, as the inbox groups: groups in the order of their most recent alert, in a
    /// group the group's own subject first and then its other subjects in the order of their most recent
    /// alert, each subject's alerts the most recent first.
    /// </summary>
    private static IReadOnlyList<(AssistantAlertDelivery Delivery, NhAssistantAlertKind Kind)> GroupedOrder(
        IReadOnlyList<(AssistantAlertDelivery Delivery, NhAssistantAlertKind Kind)> visible)
    {
        // LINQ to objects keeps the order of first appearance in GroupBy, and OrderBy is stable.
        return visible
            .GroupBy(item => GroupOf(item.Delivery.Alert!))
            .SelectMany(group => group
                .GroupBy(item => (item.Delivery.Alert!.SubjectType, item.Delivery.Alert.SubjectId))
                .OrderBy(subject => subject.Key == group.Key ? 0 : 1)
                .SelectMany(subject => subject))
            .ToArray();
    }

    /// <summary>
    /// The groups of the page that starts at <paramref name="start"/> and holds <paramref name="count"/>
    /// items of <paramref name="ordered"/>, each with its count on the page, its total, whether it goes on
    /// before or after the page, and its subjects alike.
    /// </summary>
    private IReadOnlyList<NhAssistantAlertGroupDto> GroupsOf(
        IReadOnlyList<(AssistantAlertDelivery Delivery, NhAssistantAlertKind Kind)> ordered,
        int start,
        int count)
    {
        var groups = new List<NhAssistantAlertGroupDto>();
        var end = start + count;
        var index = 0;
        while (index < ordered.Count)
        {
            var key = GroupOf(ordered[index].Delivery.Alert!);
            var first = index;
            while (index < ordered.Count && GroupOf(ordered[index].Delivery.Alert!) == key)
            {
                index++;
            }

            var onPage = Math.Min(index, end) - Math.Max(first, start);
            if (onPage <= 0)
            {
                continue;
            }

            groups.Add(GroupDtoOf(ordered, key, first, index, start, end, onPage));
        }

        return groups;
    }

    private NhAssistantAlertGroupDto GroupDtoOf(
        IReadOnlyList<(AssistantAlertDelivery Delivery, NhAssistantAlertKind Kind)> ordered,
        (string Type, string Id) key,
        int first,
        int last,
        int start,
        int end,
        int onPage)
    {
        var subjectGroups = new List<NhAssistantAlertSubjectGroupDto>();
        var index = first;
        while (index < last)
        {
            var alert = ordered[index].Delivery.Alert!;
            var subject = (alert.SubjectType, alert.SubjectId);
            var subjectFirst = index;
            while (index < last && (ordered[index].Delivery.Alert!.SubjectType, ordered[index].Delivery.Alert!.SubjectId) == subject)
            {
                index++;
            }

            var subjectOnPage = Math.Min(index, end) - Math.Max(subjectFirst, start);
            if (subjectOnPage > 0)
            {
                subjectGroups.Add(new NhAssistantAlertSubjectGroupDto(
                    subject.SubjectType,
                    subject.SubjectId,
                    subjectOnPage,
                    index - subjectFirst,
                    subjectFirst < start,
                    index > end));
            }
        }

        var (typeName, link) = NhAssistantAgentRunDtoMapper.SubjectOf(subjects, key.Type, key.Id);
        return new NhAssistantAlertGroupDto(
            key.Type,
            key.Id,
            NhAssistantAgentRunDtoMapper.DisplayNameOf(subjects, key.Type, key.Id, StoredNameOf(ordered[first].Delivery.Alert!, key)),
            onPage,
            last - first,
            first < start,
            last > end,
            subjectGroups)
        {
            SubjectTypeName = typeName,
            SubjectLink = link
        };
    }

    /// <summary>
    /// The stored name of a group's subject: the parent's name, or the subject's own without a parent.
    /// </summary>
    private static string? StoredNameOf(AssistantAlert alert, (string Type, string Id) key)
    {
        return alert.ParentSubjectType == key.Type && alert.ParentSubjectId == key.Id
            ? alert.ParentDisplayName
            : alert.SubjectDisplayName;
    }

    private static TaskResult<NhAssistantAlertDto> NotFound()
    {
        return TaskResult<NhAssistantAlertDto>.Failed(NhAssistantAlertCodes.NotFound, "The alert was not found.");
    }
}
