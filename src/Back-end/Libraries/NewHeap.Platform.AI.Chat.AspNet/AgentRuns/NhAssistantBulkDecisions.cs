using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Decides a whole filter of the approval inbox in two steps. The overview puts the pending proposals the
/// caller's inbox lists with the filter in a set, at most <see cref="NhAssistantLimits.MaxBulkDecisionProposals"/>
/// in the inbox's order, each with the version (proposal hash) the overview counted, and stores that set
/// for the caller and the caller's tenant for <see cref="NhAssistantLimits.BulkDecisionLifetime"/>. The
/// decision with the set's token decides only those proposals, never one that arrived later: it runs the
/// inbox's visibility again for the set, and decides every proposal still listed with its own rules (the
/// tenant, the view policy, its version, its expiry, four-eyes, the approver rules, the tool's approve
/// policy and the proposal guards), recording the set's id as the batch id of every decision's audit. The
/// set is claimed by one decision at a time, and a repeat of a decided token reports its stored outcome.
/// The decisions run in the request, but go on when the client goes away, so a closed page never leaves
/// half a set; a decision that stopped halfway, such as one whose process ended, is taken over by a repeat
/// after <see cref="ClaimLease"/>.
/// </summary>
internal sealed class NhAssistantBulkDecisions(
    NhAssistantRunStore runs,
    NhAssistantDecisionBatchStore batches,
    NhAssistantSystemApprovers systemApprovers,
    NhAssistantApproverCheck approvers,
    NhAssistantProposalDecisions decisions,
    NhAssistantSubjectAccessService subjects,
    IServiceProvider services)
{
    /// <summary>
    /// How long a decision may work through a set before a repeat of its token takes it over.
    /// </summary>
    internal static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The codes of a proposal that is no longer what the overview showed.
    /// </summary>
    private static readonly HashSet<string> StaleCodes = new(StringComparer.Ordinal)
    {
        NhAssistantErrorCodes.ProposalHashMismatch,
        NhAssistantErrorCodes.ApprovalNotPending,
        NhAssistantErrorCodes.ApprovalExpired,
        NhAssistantErrorCodes.ProposalStale
    };

    /// <summary>
    /// The application's clock, which a set's lifetime follows, as a proposal's does.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// The overview of the pending proposals the caller's inbox lists with <paramref name="filter"/>, and
    /// the token of its set; an empty set gets no token and stores nothing.
    /// </summary>
    public async Task<NhAssistantBulkDecisionOverviewDto> OverviewAsync(
        NhAssistantInboxFilter filter,
        string actorId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var pending = filter with { Status = NhAssistantInboxStatuses.Pending, TenantId = tenantId };
        var system = await NhAssistantAgentRunEndpoints.SystemInboxAsync(systemApprovers, pending, actorId, tenantId, cancellationToken);
        var listed = await runs.CountInboxAsync(actorId, pending, system, cancellationToken);
        if (listed == 0)
        {
            return Empty(0, 0, []);
        }

        // A tool decided one by one keeps its proposals out of the set; the overview says how many.
        var individual = approvers.IndividualDecisionToolIds();
        var excludedKinds = pending.ToolIds is { Count: > 0 } kinds
            ? individual.Where(toolId => kinds.Contains(toolId, StringComparer.Ordinal)).ToArray()
            : individual.ToArray();
        IReadOnlyList<NhAssistantInboxToolRow> excluded = excludedKinds.Length == 0
            ? []
            : await runs.CountInboxToolsAsync(actorId, pending with { ToolIds = excludedKinds }, system, cancellationToken);
        var excludedCount = excluded.Sum(tool => tool.Count);
        var set = await runs.ListDecisionSetAsync(
            actorId,
            pending with { ExcludedToolIds = individual.Count == 0 ? null : individual },
            system,
            NhAssistantLimits.MaxBulkDecisionProposals,
            cancellationToken);
        var individualDto = new NhAssistantBulkDecisionExclusionDto(
            excludedCount,
            excluded.Select(NhAssistantAgentRunEndpoints.ToToolCountDto).ToArray());
        if (set.Count == 0)
        {
            return Empty(listed, excludedCount, individualDto.Tools);
        }

        var now = _applicationClock.GetUtcNow();
        var batch = new AssistantDecisionBatch
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            TenantId = tenantId,
            ItemsJson = JsonSerializer.Serialize(
                set.Select(row => new NhAssistantDecisionSetItem(row.ApprovalId, row.ProposalHash, row.ToolId)).ToArray(),
                NhAssistantBulkDecisionJsonContext.Default.NhAssistantDecisionSetItemArray),
            ItemCount = set.Count,
            CreatedAt = now,
            ExpiresAt = now.Add(NhAssistantLimits.BulkDecisionLifetime),
            Status = NhAssistantDecisionBatchStatuses.Open
        };
        await batches.AddAsync(batch, cancellationToken);

        return new NhAssistantBulkDecisionOverviewDto(
            TokenOf(batch.Id),
            batch.ExpiresAt,
            set.Count,
            CountSubjects(set),
            listed,
            Math.Max(0, listed - excludedCount - set.Count),
            NhAssistantLimits.MaxBulkDecisionProposals,
            KindsOf(set),
            ToolsOf(set),
            SubjectTypesOf(set),
            individualDto);
    }

    /// <summary>
    /// Decides the set of <paramref name="token"/> for the caller who made its overview. An unknown token,
    /// and one of another person or tenant, is <see cref="NhAssistantErrorCodes.BulkDecisionNotFound"/>.
    /// </summary>
    public async Task<TaskResult<NhAssistantBulkDecisionResultDto>> DecideAsync(
        string token,
        bool approve,
        string? reason,
        string actorId,
        string? tenantId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var batch = Guid.TryParseExact(token, "N", out var batchId)
            ? await batches.FindAsync(batchId, cancellationToken)
            : null;
        if (batch is null
            || !string.Equals(batch.ActorId, actorId, StringComparison.Ordinal)
            || !string.Equals(batch.TenantId, tenantId, StringComparison.Ordinal))
        {
            return TaskResult<NhAssistantBulkDecisionResultDto>.Failed(NhAssistantErrorCodes.BulkDecisionNotFound, "The bulk decision was not found.");
        }
        if (batch.Status == NhAssistantDecisionBatchStatuses.Decided)
        {
            return TaskResult<NhAssistantBulkDecisionResultDto>.Succeeded(ReportOf(batch, repeated: true));
        }

        var now = _applicationClock.GetUtcNow();
        if (batch.Status == NhAssistantDecisionBatchStatuses.Open && batch.ExpiresAt <= now)
        {
            return TaskResult<NhAssistantBulkDecisionResultDto>.Failed(NhAssistantErrorCodes.BulkDecisionExpired, "The bulk decision expired.");
        }

        var resumed = batch.Status == NhAssistantDecisionBatchStatuses.Deciding;
        var decision = approve ? NhAssistantBulkDecisionChoices.Approve : NhAssistantBulkDecisionChoices.Reject;
        if (!await batches.TryClaimAsync(batch.Id, decision, now, now - ClaimLease, cancellationToken))
        {
            var current = await batches.FindAsync(batch.Id, cancellationToken);
            return current?.Status == NhAssistantDecisionBatchStatuses.Decided
                ? TaskResult<NhAssistantBulkDecisionResultDto>.Succeeded(ReportOf(current, repeated: true))
                : TaskResult<NhAssistantBulkDecisionResultDto>.Failed(NhAssistantErrorCodes.BulkDecisionInProgress, "The bulk decision is being decided.");
        }

        // A set taken over keeps the decision it began with.
        var claimed = (await batches.FindAsync(batch.Id, CancellationToken.None))!;
        approve = claimed.Decision == NhAssistantBulkDecisionChoices.Approve;
        var items = JsonSerializer.Deserialize(claimed.ItemsJson, NhAssistantBulkDecisionJsonContext.Default.NhAssistantDecisionSetItemArray) ?? [];

        // The set decides on: a closed page never leaves half of it.
        var outcomes = await DecideItemsAsync(items, approve, reason, actorId, tenantId, user, claimed.Id, resumed, CancellationToken.None);
        var resultJson = JsonSerializer.Serialize(outcomes, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantBulkDecisionItemDtoArray);
        await batches.CompleteAsync(claimed.Id, _applicationClock.GetUtcNow(), resultJson, CancellationToken.None);
        return TaskResult<NhAssistantBulkDecisionResultDto>.Succeeded(Report(claimed.Id, approve, outcomes, repeated: false));
    }

    /// <summary>
    /// Decides every proposal of the set that the caller's inbox still lists, each with its own rules and
    /// the version the overview counted; a proposal no longer listed is not decided.
    /// </summary>
    private async Task<NhAssistantBulkDecisionItemDto[]> DecideItemsAsync(
        IReadOnlyList<NhAssistantDecisionSetItem> items,
        bool approve,
        string? reason,
        string actorId,
        string? tenantId,
        ClaimsPrincipal user,
        Guid batchId,
        bool resumed,
        CancellationToken cancellationToken)
    {
        var ids = items.Select(item => item.ApprovalId).ToArray();
        var inbox = new NhAssistantInboxFilter(null, null, null, null)
        {
            Status = NhAssistantInboxStatuses.Pending,
            TenantId = tenantId
        };
        var system = await NhAssistantAgentRunEndpoints.SystemInboxAsync(systemApprovers, inbox, actorId, tenantId, cancellationToken);
        var listed = await runs.ListedAmongAsync(actorId, inbox, system, ids, cancellationToken);
        var states = await runs.GetApprovalStatesAsync(ids.Where(id => !listed.Contains(id)).ToArray(), cancellationToken);
        var origin = NhAssistantDecisionOrigin.Web with { Culture = CultureInfo.CurrentUICulture.Name, BatchId = batchId };
        var now = _applicationClock.GetUtcNow();

        var outcomes = new List<NhAssistantBulkDecisionItemDto>(items.Count);
        foreach (var item in items)
        {
            // A decision that stopped halfway decided some proposals already, as this person.
            if (resumed && await decisions.FindRepeatAsync(item.ApprovalId, actorId, approve, item.ProposalHash, cancellationToken) is { } earlier)
            {
                outcomes.Add(new NhAssistantBulkDecisionItemDto(item.ApprovalId, NhAssistantBulkDecisionOutcomes.Succeeded, null, earlier.Status));
                continue;
            }
            if (!listed.Contains(item.ApprovalId))
            {
                outcomes.Add(NotListed(item, states, now));
                continue;
            }

            var decided = await decisions.DecideAsync(
                item.ApprovalId,
                actorId,
                user,
                approve,
                item.ProposalHash,
                reason,
                origin,
                cancellationToken,
                tenantId);
            if (decided.Success)
            {
                outcomes.Add(new NhAssistantBulkDecisionItemDto(item.ApprovalId, NhAssistantBulkDecisionOutcomes.Succeeded, null, decided.Data!.Status));
                continue;
            }

            var code = NhAssistantResults.CodeOf(decided, NhAssistantErrorCodes.TurnFailed);
            outcomes.Add(new NhAssistantBulkDecisionItemDto(
                item.ApprovalId,
                StaleCodes.Contains(code) ? NhAssistantBulkDecisionOutcomes.Stale : NhAssistantBulkDecisionOutcomes.Refused,
                code,
                null));
        }

        return outcomes.ToArray();
    }

    /// <summary>
    /// A proposal of the set the caller's inbox no longer lists: stale when it is no longer pending or
    /// expired, otherwise refused as one the caller does not see, which reads as unknown.
    /// </summary>
    private static NhAssistantBulkDecisionItemDto NotListed(
        NhAssistantDecisionSetItem item,
        IReadOnlyDictionary<Guid, (string Status, DateTimeOffset ExpiresAt)> states,
        DateTimeOffset now)
    {
        if (states.TryGetValue(item.ApprovalId, out var state) && state.Status != NhAssistantApprovalStatuses.Pending)
        {
            return new NhAssistantBulkDecisionItemDto(item.ApprovalId, NhAssistantBulkDecisionOutcomes.Stale, NhAssistantErrorCodes.ApprovalNotPending, null);
        }
        if (states.TryGetValue(item.ApprovalId, out state) && state.ExpiresAt <= now)
        {
            return new NhAssistantBulkDecisionItemDto(item.ApprovalId, NhAssistantBulkDecisionOutcomes.Stale, NhAssistantErrorCodes.ApprovalExpired, null);
        }

        return new NhAssistantBulkDecisionItemDto(item.ApprovalId, NhAssistantBulkDecisionOutcomes.Refused, NhAssistantErrorCodes.ApprovalNotFound, null);
    }

    private static NhAssistantBulkDecisionResultDto ReportOf(AssistantDecisionBatch batch, bool repeated)
    {
        var outcomes = batch.ResultJson is null
            ? []
            : JsonSerializer.Deserialize(batch.ResultJson, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantBulkDecisionItemDtoArray) ?? [];
        return Report(batch.Id, batch.Decision == NhAssistantBulkDecisionChoices.Approve, outcomes, repeated);
    }

    private static NhAssistantBulkDecisionResultDto Report(
        Guid batchId,
        bool approve,
        IReadOnlyList<NhAssistantBulkDecisionItemDto> outcomes,
        bool repeated)
    {
        return new NhAssistantBulkDecisionResultDto(
            TokenOf(batchId),
            approve ? NhAssistantBulkDecisionChoices.Approve : NhAssistantBulkDecisionChoices.Reject,
            outcomes.Count(outcome => outcome.Outcome == NhAssistantBulkDecisionOutcomes.Succeeded),
            outcomes.Count(outcome => outcome.Outcome == NhAssistantBulkDecisionOutcomes.Refused),
            outcomes.Count(outcome => outcome.Outcome == NhAssistantBulkDecisionOutcomes.Stale),
            repeated,
            outcomes);
    }

    private static NhAssistantBulkDecisionOverviewDto Empty(
        int listed,
        int excluded,
        IReadOnlyList<NhAssistantInboxToolCountDto> excludedTools)
    {
        return new NhAssistantBulkDecisionOverviewDto(
            null,
            null,
            0,
            0,
            listed,
            0,
            NhAssistantLimits.MaxBulkDecisionProposals,
            [],
            [],
            [],
            new NhAssistantBulkDecisionExclusionDto(excluded, excludedTools));
    }

    private static string TokenOf(Guid batchId)
    {
        return batchId.ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Scope keys keep equal ids of different tenants or divisions apart.
    /// </summary>
    private static int CountSubjects(IEnumerable<NhAssistantDecisionSetRow> rows)
    {
        return rows.Select(row => (row.SubjectType, row.ScopeKey, row.SubjectId)).Distinct().Count();
    }

    /// <summary>
    /// The name a card shows for the kind: the presentation of the first proposal of the kind that has one,
    /// in the caller's language, otherwise the tool id.
    /// </summary>
    private static string ToolNameOf(IEnumerable<NhAssistantDecisionSetRow> rows, string toolId)
    {
        var presented = rows.FirstOrDefault(row => row.PresentationJson is not null);
        var presentation = presented is null
            ? null
            : Runtime.NhAssistantToolPresentationResolver.Pick(presented.PresentationJson, presented.PresentationsJson, CultureInfo.CurrentUICulture);
        return presentation?.ToolDisplayName ?? toolId;
    }

    private IReadOnlyList<NhAssistantBulkDecisionKindDto> KindsOf(IReadOnlyList<NhAssistantDecisionSetRow> set)
    {
        return set
            .GroupBy(row => (row.ToolId, row.SubjectType))
            .Select(kind => new NhAssistantBulkDecisionKindDto(
                kind.Key.ToolId,
                ToolNameOf(kind, kind.Key.ToolId),
                kind.Key.SubjectType,
                NhAssistantAgentRunEndpoints.TypeNameOf(subjects, kind.Key.SubjectType),
                kind.Count(),
                CountSubjects(kind)))
            .OrderByDescending(kind => kind.Count)
            .ThenBy(kind => kind.ToolId, StringComparer.Ordinal)
            .ThenBy(kind => kind.SubjectType, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<NhAssistantInboxToolCountDto> ToolsOf(IReadOnlyList<NhAssistantDecisionSetRow> set)
    {
        return set
            .GroupBy(row => row.ToolId, StringComparer.Ordinal)
            .Select(tool => new NhAssistantInboxToolCountDto(tool.Key, ToolNameOf(tool, tool.Key), tool.Count()))
            .OrderByDescending(tool => tool.Count)
            .ThenBy(tool => tool.ToolId, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<NhAssistantInboxSubjectTypeCountDto> SubjectTypesOf(IReadOnlyList<NhAssistantDecisionSetRow> set)
    {
        return set
            .GroupBy(row => row.SubjectType, StringComparer.Ordinal)
            .Select(type => new NhAssistantInboxSubjectTypeCountDto(
                type.Key,
                NhAssistantAgentRunEndpoints.TypeNameOf(subjects, type.Key),
                type.Count(),
                CountSubjects(type)))
            .OrderByDescending(type => type.Count)
            .ThenBy(type => type.SubjectType, StringComparer.Ordinal)
            .ToArray();
    }
}

/// <summary>
/// The decisions of a bulk decision, as the requests and the results name them.
/// </summary>
internal static class NhAssistantBulkDecisionChoices
{
    public const string Approve = "approve";
    public const string Reject = "reject";
}

/// <summary>
/// One proposal of a stored set: its id, the version the overview counted and its kind of action.
/// </summary>
internal sealed record NhAssistantDecisionSetItem(Guid ApprovalId, string ProposalHash, string ToolId);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(NhAssistantDecisionSetItem[]))]
internal sealed partial class NhAssistantBulkDecisionJsonContext : JsonSerializerContext;
