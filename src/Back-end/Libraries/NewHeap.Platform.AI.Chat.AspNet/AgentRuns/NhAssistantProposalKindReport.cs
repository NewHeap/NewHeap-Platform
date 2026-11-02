using System.Globalization;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The view per kind of action over all agents (FB82): per tool the counts, the rejection rate and
/// how long people take to decide, and per agent and tool whether the numbers make the kind a
/// candidate for running without approval by the thresholds of
/// <see cref="NhAssistantRoutineCandidateOptions"/>. It reads only; nothing about deciding changes.
/// </summary>
internal sealed class NhAssistantProposalKindReport(
    NhAssistantDecisionNumbersStore store,
    NhAssistantAgentCatalog agents,
    IOptionsMonitor<NhAssistantOptions> options)
{
    public async Task<NhAssistantProposalKindsDto> GetAsync(
        NhAssistantProposalKindFilter filter,
        CancellationToken cancellationToken)
    {
        var thresholds = options.CurrentValue.RoutineCandidates.Bounded();
        var aggregates = await store.GetKindAggregatesAsync(
            filter,
            NhAssistantDecisionTimes.SamplePerGroup,
            cancellationToken);
        var known = (await agents.ListAsync(includeDisabled: true, cancellationToken))
            .ToDictionary(agent => agent.Id, StringComparer.Ordinal);
        var names = aggregates.Presentations.ToDictionary(
            row => row.ToolId,
            row => NhAssistantToolPresentationResolver.Pick(
                row.PresentationJson,
                row.PresentationsJson,
                CultureInfo.CurrentUICulture)?.ToolDisplayName,
            StringComparer.Ordinal);

        var kinds = new List<NhAssistantProposalKindDto>();
        foreach (var tool in aggregates.Statuses.GroupBy(row => row.ToolId, StringComparer.Ordinal))
        {
            var candidates = new List<NhAssistantRoutineCandidateDto>();
            var kindSample = new List<NhAssistantDecisionTimeRow>();
            var kindSampled = false;
            foreach (var agent in tool.GroupBy(row => row.AgentId, StringComparer.Ordinal))
            {
                var (sample, sampled) = NhAssistantDecisionTimes.SampleOf(aggregates.DecisionTimes
                    .Where(row => row.AgentId == agent.Key && row.ToolId == tool.Key)
                    .ToList());
                kindSample.AddRange(sample);
                kindSampled |= sampled;
                var numbers = Numbers(
                    agent.ToList(),
                    NhAssistantDecisionTimes.Of(sample, sampled),
                    aggregates.LastRejections
                        .Where(row => row.AgentId == agent.Key && row.ToolId == tool.Key)
                        .Select(row => (DateTimeOffset?)row.DecidedAt)
                        .Max());
                var unmet = UnmetThresholds(numbers, thresholds, filter.To);
                known.TryGetValue(agent.Key, out var definition);
                candidates.Add(new NhAssistantRoutineCandidateDto(
                    agent.Key,
                    definition?.Definition.DisplayNameKey,
                    definition?.Source,
                    tool.Key,
                    numbers,
                    unmet.Count == 0,
                    unmet));
            }

            // Across agents, each agent's sample counts; the kind is sampled when one of them is.
            kinds.Add(new NhAssistantProposalKindDto(
                tool.Key,
                names.GetValueOrDefault(tool.Key) ?? tool.Key,
                Numbers(
                    tool.ToList(),
                    NhAssistantDecisionTimes.Of(kindSample, kindSampled),
                    candidates.Select(candidate => candidate.Numbers.LastRejectedAt).Max()),
                candidates
                    .OrderByDescending(candidate => candidate.Numbers.Decisions)
                    .ThenBy(candidate => candidate.AgentId, StringComparer.Ordinal)
                    .ToArray()));
        }

        return new NhAssistantProposalKindsDto(
            filter.From,
            filter.To,
            new NhAssistantRoutineCandidateThresholdsDto(
                thresholds.MinimumDecisions,
                thresholds.MinimumApprovalRate,
                thresholds.QuietPeriod.TotalDays,
                thresholds.MaximumStaleOrFailedAfterApproval),
            kinds
                .OrderByDescending(kind => kind.Numbers.Decisions)
                .ThenBy(kind => kind.ToolId, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// The thresholds the numbers miss, in the order the view explains them; empty for a candidate.
    /// </summary>
    internal static IReadOnlyList<string> UnmetThresholds(
        NhAssistantProposalKindNumbersDto numbers,
        NhAssistantRoutineCandidateOptions thresholds,
        DateTimeOffset periodEnd)
    {
        var unmet = new List<string>();
        if (numbers.Decisions < thresholds.MinimumDecisions)
        {
            unmet.Add(NhAssistantRoutineCandidateReasons.TooFewDecisions);
        }
        if (numbers.ApprovalRate is not { } approvalRate || approvalRate < thresholds.MinimumApprovalRate)
        {
            unmet.Add(NhAssistantRoutineCandidateReasons.ApprovalRateTooLow);
        }
        if (numbers.LastRejectedAt is { } lastRejectedAt && lastRejectedAt >= periodEnd - thresholds.QuietPeriod)
        {
            unmet.Add(NhAssistantRoutineCandidateReasons.RecentRejection);
        }
        if (numbers.StaleAfterApproval + numbers.ExecutionFailed > thresholds.MaximumStaleOrFailedAfterApproval)
        {
            unmet.Add(NhAssistantRoutineCandidateReasons.StaleOrFailedAfterApproval);
        }

        return unmet;
    }

    private static NhAssistantProposalKindNumbersDto Numbers(
        IReadOnlyList<NhAssistantKindStatusCount> statuses,
        NhAssistantDecisionTimeDto? decisionTime,
        DateTimeOffset? lastRejectedAt)
    {
        int Count(Func<NhAssistantKindStatusCount, bool> selected)
        {
            return statuses.Where(selected).Sum(row => row.Count);
        }

        var staleAfterApproval = Count(row => row.Status == NhAssistantApprovalStatuses.Stale && row.AfterApproval);
        var approved = Count(row => NhAssistantDecisionTimes.IsApproved(row.Status)) + staleAfterApproval;
        var rejected = Count(row => row.Status == NhAssistantApprovalStatuses.Rejected);
        var decisions = approved + rejected;
        return new NhAssistantProposalKindNumbersDto(
            Count(row => row.Status == NhAssistantApprovalStatuses.Pending),
            approved,
            rejected,
            decisions,
            Count(row => row.Status == NhAssistantApprovalStatuses.Expired),
            Count(row => row.Status == NhAssistantApprovalStatuses.Withdrawn),
            Count(row => row.Status == NhAssistantApprovalStatuses.Stale && !row.AfterApproval),
            staleAfterApproval,
            Count(row => row.Status == NhAssistantApprovalStatuses.ExecutionFailed),
            decisions == 0 ? null : (double)approved / decisions,
            decisions == 0 ? null : (double)rejected / decisions,
            decisionTime,
            lastRejectedAt);
    }
}

/// <summary>
/// The median and the 90th percentile of the time from a proposal to a person's decision, computed
/// from the bounded sample <see cref="NhAssistantDecisionNumbersStore"/> loads: per group at most
/// <see cref="SamplePerGroup"/> of the newest decisions.
/// </summary>
internal static class NhAssistantDecisionTimes
{
    /// <summary>The most decisions per group the times are computed from.</summary>
    public const int SamplePerGroup = 1_000;

    public static bool IsApproved(string status)
    {
        return status is NhAssistantApprovalStatuses.Approved
            or NhAssistantApprovalStatuses.Executing
            or NhAssistantApprovalStatuses.AwaitingApplication
            or NhAssistantApprovalStatuses.Executed
            or NhAssistantApprovalStatuses.ExecutionFailed
            or NhAssistantApprovalStatuses.ExecutionUnknown
            or NhAssistantApprovalStatuses.Resolved;
    }

    /// <summary>
    /// The sample of one group's decisions, of which the store loaded at most one more than
    /// <see cref="SamplePerGroup"/> to tell whether the group has more: the newest decisions, and
    /// whether some were left out.
    /// </summary>
    public static (IReadOnlyList<NhAssistantDecisionTimeRow> Sample, bool Sampled) SampleOf(
        IReadOnlyList<NhAssistantDecisionTimeRow> loaded)
    {
        if (loaded.Count <= SamplePerGroup)
        {
            return (loaded, false);
        }

        return (loaded.OrderByDescending(row => row.DecidedAt).Take(SamplePerGroup).ToList(), true);
    }

    /// <summary>
    /// The decision time of a sample of decisions; <see langword="null"/> without decisions.
    /// </summary>
    public static NhAssistantDecisionTimeDto? Of(IReadOnlyCollection<NhAssistantDecisionTimeRow> sample, bool sampled)
    {
        if (sample.Count == 0)
        {
            return null;
        }

        var seconds = sample
            .Select(row => Math.Max(0, (row.DecidedAt - row.CreatedAt).TotalSeconds))
            .Order()
            .ToList();
        return new NhAssistantDecisionTimeDto(
            seconds.Count,
            WholeSeconds(Percentile(seconds, 0.5)),
            WholeSeconds(Percentile(seconds, 0.9)),
            sampled);
    }

    /// <summary>
    /// The percentile of sorted values with linear interpolation between the nearest ranks, as
    /// <c>percentile_cont</c> computes it.
    /// </summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Count - 1) * fraction;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    private static int WholeSeconds(double seconds)
    {
        return (int)Math.Min(int.MaxValue, Math.Round(seconds, MidpointRounding.AwayFromZero));
    }
}
