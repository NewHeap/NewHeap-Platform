using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// How a shadow variant compared with its live agent over a period: the runs that handled the
/// same events, the proposals per tool with what people decided about the live ones, what each
/// cost and how each routed. Counts, identifiers and links only; no messages or arguments.
/// </summary>
/// <param name="Differences">Proposals only one of the two made, with the runs and threads to read, at most 50.</param>
public sealed record NhAssistantEvaluationDto(
    string AgentId,
    string VariantId,
    DateTimeOffset From,
    DateTimeOffset To,
    NhAssistantEvaluationRunsDto Runs,
    IReadOnlyList<NhAssistantEvaluationToolDto> Tools,
    NhAssistantEvaluationCostDto LiveCost,
    NhAssistantEvaluationCostDto VariantCost,
    IReadOnlyList<NhAssistantEvaluationRoutingDto> Routing,
    IReadOnlyList<NhAssistantEvaluationDifferenceDto> Differences);

/// <param name="Paired">Pairs of a live and a variant run that handled the same event or replay.</param>
/// <param name="Replayed">The pairs whose variant run replayed the live run; they saw today's state of the subject.</param>
/// <param name="LiveUnpaired">Live runs no variant run handled, such as runs people started.</param>
/// <param name="Truncated">The period held more runs than one evaluation reads; it covers the oldest.</param>
public sealed record NhAssistantEvaluationRunsDto(
    int Live,
    int Variant,
    int Paired,
    int Replayed,
    int LiveUnpaired,
    int VariantUnpaired,
    bool Truncated);

/// <summary>
/// The proposals of paired runs for one tool. A live and a variant proposal agree when they have
/// the same arguments, or change the same objects.
/// </summary>
/// <param name="RejectedLiveNotProposed">Live proposals people rejected that the variant did not make.</param>
/// <param name="RejectedLiveAlsoProposed">Live proposals people rejected that the variant made too.</param>
/// <param name="ApprovedLiveMissed">Live proposals people approved that the variant did not make.</param>
public sealed record NhAssistantEvaluationToolDto(
    string ToolId,
    int Agreed,
    int LiveOnly,
    int VariantOnly,
    NhAssistantEvaluationDecisionsDto LiveDecisions,
    int RejectedLiveNotProposed,
    int RejectedLiveAlsoProposed,
    int ApprovedLiveMissed);

/// <summary>
/// What people decided about the live proposals of a tool; executed includes failed and uncertain
/// executions, other includes expired and withdrawn proposals.
/// </summary>
/// <param name="RejectionCategories">The feedback categories of rejected proposals and how often people chose them.</param>
public sealed record NhAssistantEvaluationDecisionsDto(
    int Pending,
    int Approved,
    int Executed,
    int Rejected,
    int Stale,
    int Other,
    IReadOnlyDictionary<string, int> RejectionCategories);

/// <summary>
/// What one side's runs in the evaluation cost, in total, on average and at the 95th percentile.
/// </summary>
public sealed record NhAssistantEvaluationCostDto(
    int Runs,
    long ModelCalls,
    long Tokens,
    long ToolCalls,
    double AverageModelCalls,
    double AverageTokens,
    double AverageToolCalls,
    long P95ModelCalls,
    long P95Tokens,
    long P95ToolCalls);

/// <summary>
/// How many pairs routed to these skills; <see langword="null"/> is the agent's own tools.
/// </summary>
public sealed record NhAssistantEvaluationRoutingDto(
    string? LiveSkill,
    string? VariantSkill,
    int Runs);

/// <param name="Kind"><c>live-only</c> or <c>variant-only</c>.</param>
/// <param name="LiveStatus">What people decided about a live-only proposal.</param>
public sealed record NhAssistantEvaluationDifferenceDto(
    string ToolId,
    string Kind,
    string? LiveStatus,
    Guid LiveRunId,
    Guid LiveConversationId,
    Guid VariantRunId,
    Guid VariantConversationId,
    bool Replayed);

public static class NhAssistantEvaluationDifferenceKinds
{
    public const string LiveOnly = "live-only";
    public const string VariantOnly = "variant-only";
}

/// <summary>
/// One pair of the evaluation as the JSON Lines export writes it: identifiers, statuses, usage and
/// the proposals' tools, statuses and argument hashes. No messages or arguments.
/// </summary>
/// <param name="AgreedTools">The tools of the proposals both made.</param>
public sealed record NhAssistantEvaluationExportRecord(
    Guid LiveRunId,
    Guid VariantRunId,
    bool Replayed,
    string SubjectType,
    string SubjectId,
    string TriggerId,
    NhAssistantEvaluationRunRecord Live,
    NhAssistantEvaluationRunRecord Variant,
    IReadOnlyList<string> AgreedTools);

public sealed record NhAssistantEvaluationRunRecord(
    Guid ConversationId,
    string Status,
    string? Skill,
    string? SkillChosenBy,
    int ModelCalls,
    long Tokens,
    int ToolCalls,
    IReadOnlyList<NhAssistantEvaluationProposalRecord> Proposals);

public sealed record NhAssistantEvaluationProposalRecord(
    string ToolId,
    string Status,
    string? ArgumentsHash);

/// <summary>
/// Starts a replay of recorded runs into a shadow variant.
/// </summary>
/// <param name="SourceAgentId">The agent whose runs to replay; the live agent of the variant by default.</param>
/// <param name="From">The start of the period; seven days before <paramref name="To"/> by default.</param>
/// <param name="To">The end of the period, exclusive; now by default.</param>
/// <param name="MaxRuns">The most runs to replay, 1 to 500; 100 by default.</param>
public sealed record NhAssistantReplayRequest(
    string? SourceAgentId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int? MaxRuns);

/// <param name="Status"><c>running</c>, <c>completed</c> or <c>stopped</c>.</param>
/// <param name="StopReason">Why a stopped replay stopped, such as <c>assistant-budget-exhausted</c>.</param>
/// <param name="RunsReplayed">The recorded runs fired into the variant so far.</param>
/// <param name="RunsSkipped">The recorded runs passed over: without inputs, or whose subject is gone.</param>
public sealed record NhAssistantReplayDto(
    Guid Id,
    string VariantAgentId,
    string SourceAgentId,
    DateTimeOffset From,
    DateTimeOffset To,
    int MaxRuns,
    string Status,
    string? StopReason,
    int RunsReplayed,
    int RunsSkipped,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt);

public sealed record NhAssistantReplayListDto(IReadOnlyList<NhAssistantReplayDto> Items);

/// <summary>
/// The background-operation payload of a replay.
/// </summary>
public sealed record NhAssistantReplayOperationRequest(Guid ReplayId);

/// <summary>
/// Compares a shadow variant with its live agent: it pairs the runs that handled the same event
/// (same subject, trigger and dedupe key) or that a replay repeated, and matches their proposals by
/// tool and arguments hash, or tool and objects.
/// </summary>
internal sealed class NhAssistantEvaluationService(
    NhAssistantEvaluationStore store,
    NhAssistantRegistrationState registration)
{
    private const int MaxDifferences = 50;

    public bool IsConfigured(string agentId)
    {
        return registration.AgentRuns.ContainsKey(agentId);
    }

    public bool IsVariant(string variantId)
    {
        return registration.AgentRuns.TryGetValue(variantId, out var options) && options.Shadow;
    }

    public async Task<NhAssistantEvaluationDto> CompareAsync(
        string agentId,
        string variantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var comparison = await BuildAsync(agentId, variantId, from, to, cancellationToken);
        var tools = new Dictionary<string, ToolTally>(StringComparer.Ordinal);
        var differences = new List<NhAssistantEvaluationDifferenceDto>();
        foreach (var pair in comparison.Pairs)
        {
            foreach (var match in pair.Matches)
            {
                var tally = Tally(tools, match.Live?.ToolId ?? match.Variant!.ToolId);
                if (match.Live is { } live)
                {
                    tally.Count(live.Status, comparison.Categories[live.ApprovalId]);
                }
                if (match.Live is not null && match.Variant is not null)
                {
                    tally.Agreed++;
                    if (match.Live.Status == NhAssistantApprovalStatuses.Rejected)
                    {
                        tally.RejectedLiveAlsoProposed++;
                    }
                    continue;
                }

                if (match.Live is { } only)
                {
                    tally.LiveOnly++;
                    if (only.Status == NhAssistantApprovalStatuses.Rejected)
                    {
                        tally.RejectedLiveNotProposed++;
                    }
                    else if (IsApproved(only.Status))
                    {
                        tally.ApprovedLiveMissed++;
                    }
                }
                else
                {
                    tally.VariantOnly++;
                }
                if (differences.Count < MaxDifferences)
                {
                    differences.Add(new NhAssistantEvaluationDifferenceDto(
                        match.Live?.ToolId ?? match.Variant!.ToolId,
                        match.Live is null ? NhAssistantEvaluationDifferenceKinds.VariantOnly : NhAssistantEvaluationDifferenceKinds.LiveOnly,
                        match.Live?.Status,
                        pair.Live.Id,
                        pair.Live.ConversationId,
                        pair.Variant.Id,
                        pair.Variant.ConversationId,
                        pair.Replayed));
                }
            }
        }

        var pairedVariants = comparison.Pairs.Select(pair => pair.Variant.Id).ToHashSet();
        var pairedLive = comparison.Pairs.Select(pair => pair.Live.Id).ToHashSet();
        return new NhAssistantEvaluationDto(
            agentId,
            variantId,
            from,
            to,
            new NhAssistantEvaluationRunsDto(
                comparison.LiveRuns.Count,
                comparison.VariantRuns.Count,
                comparison.Pairs.Count,
                comparison.Pairs.Count(pair => pair.Replayed),
                comparison.LiveRuns.Count(run => !pairedLive.Contains(run.Id)),
                comparison.VariantRuns.Count(run => !pairedVariants.Contains(run.Id)),
                comparison.Truncated),
            tools.Values
                .OrderBy(tally => tally.ToolId, StringComparer.Ordinal)
                .Select(tally => tally.ToDto())
                .ToArray(),
            Cost(comparison.LiveRuns),
            Cost(comparison.VariantRuns),
            comparison.Pairs
                .GroupBy(pair => (pair.Live.SkillId, pair.Variant.SkillId))
                .OrderByDescending(group => group.Count())
                .Select(group => new NhAssistantEvaluationRoutingDto(group.Key.Item1, group.Key.Item2, group.Count()))
                .ToArray(),
            differences);
    }

    /// <summary>
    /// The pairs of the evaluation as export records, oldest live run first.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantEvaluationExportRecord>> ExportAsync(
        string agentId,
        string variantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var comparison = await BuildAsync(agentId, variantId, from, to, cancellationToken);
        return comparison.Pairs
            .Select(pair => new NhAssistantEvaluationExportRecord(
                pair.Live.Id,
                pair.Variant.Id,
                pair.Replayed,
                pair.Live.SubjectType,
                pair.Live.SubjectId,
                pair.Live.TriggerId,
                Record(pair.Live, pair.Matches.Select(match => match.Live)),
                Record(pair.Variant, pair.Matches.Select(match => match.Variant)),
                pair.Matches
                    .Where(match => match.Live is not null && match.Variant is not null)
                    .Select(match => match.Live!.ToolId)
                    .ToArray()))
            .ToArray();
    }

    private async Task<Comparison> BuildAsync(
        string agentId,
        string variantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var max = NhAssistantEvaluationLimits.MaxEvaluatedRuns;
        var live = (await store.GetRunsAsync(agentId, from, to, max + 1, cancellationToken)).ToList();
        // The live agent's live runs against the variant's shadow runs.
        var variant = (await store.GetRunsAsync(variantId, from, to, max + 1, cancellationToken, shadow: true)).ToList();
        var truncated = live.Count > max || variant.Count > max;
        live = live.Take(max).ToList();
        variant = variant.Take(max).ToList();
        // Replays of the period ran later; they belong to the runs they replay.
        var known = variant.Select(run => run.Id).ToHashSet();
        variant.AddRange((await store.GetReplaysOfAsync(variantId, live.Select(run => run.Id).ToArray(), cancellationToken))
            .Where(run => known.Add(run.Id)));

        var replays = variant
            .Where(run => run.ReplayOfRunId is not null)
            .ToLookup(run => run.ReplayOfRunId!.Value);
        var byEvent = variant
            .Where(run => run.ReplayOfRunId is null && run.TriggerEventKey is not null)
            .GroupBy(EventOf)
            .ToDictionary(group => group.Key, group => new Queue<NhAssistantEvaluationRun>(group.OrderBy(run => run.CreatedAt)));
        var pairs = new List<(NhAssistantEvaluationRun Live, NhAssistantEvaluationRun Variant, bool Replayed)>();
        foreach (var run in live)
        {
            pairs.AddRange(replays[run.Id].Select(replayed => (run, replayed, true)));
            if (run.TriggerEventKey is not null && byEvent.TryGetValue(EventOf(run), out var queue) && queue.Count > 0)
            {
                pairs.Add((run, queue.Dequeue(), false));
            }
        }

        var runIds = pairs.SelectMany(pair => new[] { pair.Live.Id, pair.Variant.Id }).Distinct().ToArray();
        var proposals = await store.GetProposalsAsync(runIds, cancellationToken);
        var objects = await store.GetObjectsAsync(proposals.Select(proposal => proposal.ApprovalId).ToArray(), cancellationToken);
        var byRun = proposals.ToLookup(proposal => proposal.RunId);
        var categories = await store.GetFeedbackCategoriesAsync(
            pairs.SelectMany(pair => byRun[pair.Live.Id]).Select(proposal => proposal.ApprovalId).Distinct().ToArray(),
            cancellationToken);
        var compared = pairs
            .Select(pair => new Pair(
                pair.Live,
                pair.Variant,
                pair.Replayed,
                Match(byRun[pair.Live.Id].ToList(), byRun[pair.Variant.Id].ToList(), objects)))
            .ToArray();
        return new Comparison(live, variant, compared, categories, truncated);
    }

    /// <summary>
    /// Pairs each live proposal with a variant proposal of the same tool and arguments hash, then of
    /// the same tool and objects; the rest are one side's only.
    /// </summary>
    private static IReadOnlyList<ProposalMatch> Match(
        List<NhAssistantEvaluationProposal> live,
        List<NhAssistantEvaluationProposal> variant,
        ILookup<Guid, string> objects)
    {
        var matches = new List<ProposalMatch>();
        var open = new List<NhAssistantEvaluationProposal>(variant);
        foreach (var proposal in live)
        {
            var same = open.FirstOrDefault(candidate => candidate.ToolId == proposal.ToolId
                    && candidate.ArgumentsHash is not null
                    && candidate.ArgumentsHash == proposal.ArgumentsHash)
                ?? open.FirstOrDefault(candidate => candidate.ToolId == proposal.ToolId && SameObjects(objects, candidate, proposal));
            if (same is not null)
            {
                open.Remove(same);
            }

            matches.Add(new ProposalMatch(proposal, same));
        }

        matches.AddRange(open.Select(proposal => new ProposalMatch(null, proposal)));
        return matches;
    }

    private static bool SameObjects(
        ILookup<Guid, string> objects,
        NhAssistantEvaluationProposal left,
        NhAssistantEvaluationProposal right)
    {
        var first = objects[left.ApprovalId].ToHashSet(StringComparer.Ordinal);
        return first.Count > 0 && first.SetEquals(objects[right.ApprovalId]);
    }

    private static (string, string, string, string) EventOf(NhAssistantEvaluationRun run)
    {
        return (run.SubjectType, run.SubjectId, run.TriggerId, run.TriggerEventKey!);
    }

    private static bool IsApproved(string status)
    {
        return status is NhAssistantApprovalStatuses.Approved
            or NhAssistantApprovalStatuses.Executing
            or NhAssistantApprovalStatuses.AwaitingApplication
            or NhAssistantApprovalStatuses.Executed
            or NhAssistantApprovalStatuses.ExecutionFailed
            or NhAssistantApprovalStatuses.ExecutionUnknown
            or NhAssistantApprovalStatuses.Resolved;
    }

    private static NhAssistantEvaluationRunRecord Record(
        NhAssistantEvaluationRun run,
        IEnumerable<NhAssistantEvaluationProposal?> proposals)
    {
        return new NhAssistantEvaluationRunRecord(
            run.ConversationId,
            run.Status,
            run.SkillId,
            run.SkillChosenBy,
            run.ModelCalls,
            run.Tokens,
            run.ToolCalls,
            proposals
                .OfType<NhAssistantEvaluationProposal>()
                .Select(proposal => new NhAssistantEvaluationProposalRecord(proposal.ToolId, proposal.Status, proposal.ArgumentsHash))
                .ToArray());
    }

    private static NhAssistantEvaluationCostDto Cost(IReadOnlyList<NhAssistantEvaluationRun> runs)
    {
        if (runs.Count == 0)
        {
            return new NhAssistantEvaluationCostDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        long[] modelCalls = runs.Select(run => (long)run.ModelCalls).ToArray();
        long[] tokens = runs.Select(run => run.Tokens).ToArray();
        long[] toolCalls = runs.Select(run => (long)run.ToolCalls).ToArray();
        return new NhAssistantEvaluationCostDto(
            runs.Count,
            modelCalls.Sum(),
            tokens.Sum(),
            toolCalls.Sum(),
            Math.Round(modelCalls.Average(), 2),
            Math.Round(tokens.Average(), 2),
            Math.Round(toolCalls.Average(), 2),
            Percentile95(modelCalls),
            Percentile95(tokens),
            Percentile95(toolCalls));
    }

    /// <summary>
    /// The nearest-rank 95th percentile.
    /// </summary>
    private static long Percentile95(long[] values)
    {
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(0.95 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static ToolTally Tally(Dictionary<string, ToolTally> tools, string toolId)
    {
        if (!tools.TryGetValue(toolId, out var tally))
        {
            tally = new ToolTally(toolId);
            tools.Add(toolId, tally);
        }

        return tally;
    }

    private sealed record ProposalMatch(NhAssistantEvaluationProposal? Live, NhAssistantEvaluationProposal? Variant);

    private sealed record Pair(
        NhAssistantEvaluationRun Live,
        NhAssistantEvaluationRun Variant,
        bool Replayed,
        IReadOnlyList<ProposalMatch> Matches);

    private sealed record Comparison(
        IReadOnlyList<NhAssistantEvaluationRun> LiveRuns,
        IReadOnlyList<NhAssistantEvaluationRun> VariantRuns,
        IReadOnlyList<Pair> Pairs,
        ILookup<Guid, string> Categories,
        bool Truncated);

    private sealed class ToolTally(string toolId)
    {
        private readonly Dictionary<string, int> _categories = new(StringComparer.Ordinal);
        private int _pending;
        private int _approved;
        private int _executed;
        private int _rejected;
        private int _stale;
        private int _other;

        public string ToolId { get; } = toolId;

        public int Agreed { get; set; }

        public int LiveOnly { get; set; }

        public int VariantOnly { get; set; }

        public int RejectedLiveNotProposed { get; set; }

        public int RejectedLiveAlsoProposed { get; set; }

        public int ApprovedLiveMissed { get; set; }

        public void Count(string status, IEnumerable<string> categories)
        {
            switch (status)
            {
                case NhAssistantApprovalStatuses.Pending:
                    _pending++;
                    break;
                case NhAssistantApprovalStatuses.Approved
                    or NhAssistantApprovalStatuses.Executing
                    or NhAssistantApprovalStatuses.AwaitingApplication:
                    _approved++;
                    break;
                case NhAssistantApprovalStatuses.Executed
                    or NhAssistantApprovalStatuses.ExecutionFailed
                    or NhAssistantApprovalStatuses.ExecutionUnknown
                    or NhAssistantApprovalStatuses.Resolved:
                    _executed++;
                    break;
                case NhAssistantApprovalStatuses.Rejected:
                    _rejected++;
                    foreach (var category in categories)
                    {
                        _categories[category] = _categories.GetValueOrDefault(category) + 1;
                    }
                    break;
                case NhAssistantApprovalStatuses.Stale:
                    _stale++;
                    break;
                default:
                    _other++;
                    break;
            }
        }

        public NhAssistantEvaluationToolDto ToDto()
        {
            return new NhAssistantEvaluationToolDto(
                ToolId,
                Agreed,
                LiveOnly,
                VariantOnly,
                new NhAssistantEvaluationDecisionsDto(_pending, _approved, _executed, _rejected, _stale, _other, _categories),
                RejectedLiveNotProposed,
                RejectedLiveAlsoProposed,
                ApprovedLiveMissed);
        }
    }
}

/// <summary>
/// Starts replays of recorded runs into shadow variants: one active replay per variant, at most
/// <see cref="NhAssistantEvaluationLimits.MaxReplayRuns"/> runs, as a background operation.
/// </summary>
internal sealed class NhAssistantReplayService(
    NhAssistantRegistrationState registration,
    NhAssistantEvaluationStore store,
    NhAssistantAgentEvents events,
    NhAssistantShadowModes shadowModes,
    IServiceProvider services,
    ILogger<NhAssistantReplayService> logger)
{
    private const int DefaultMaxRuns = 100;
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(7);

    public async Task<TaskResult<AssistantAgentReplay>> StartAsync(
        string variantId,
        NhAssistantReplayRequest request,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (!registration.AgentRuns.TryGetValue(variantId, out var options))
        {
            return TaskResult<AssistantAgentReplay>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent has no run configuration.");
        }

        // A replay runs only into an agent that works in shadow now, never into a live one.
        var sourceId = request.SourceAgentId ?? options.ShadowOfAgentId;
        if (!await shadowModes.IsAgentShadowAsync(variantId, cancellationToken)
            || sourceId is null
            || string.Equals(sourceId, variantId, StringComparison.Ordinal))
        {
            return TaskResult<AssistantAgentReplay>.Failed(
                NhAssistantErrorCodes.ReplayNotShadow,
                "Replays run into a shadow variant, from another agent.");
        }

        var to = (request.To ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var from = (request.From ?? to - DefaultPeriod).ToUniversalTime();
        var maxRuns = request.MaxRuns ?? DefaultMaxRuns;
        if (!registration.AgentRuns.ContainsKey(sourceId)
            || from >= to
            || to - from > NhAssistantEvaluationLimits.MaxPeriod
            || maxRuns is < 1 or > NhAssistantEvaluationLimits.MaxReplayRuns)
        {
            return TaskResult<AssistantAgentReplay>.Failed(NhAssistantErrorCodes.MessageInvalid, "The replay request is not valid.");
        }
        if (await store.FindActiveReplayAsync(variantId, cancellationToken) is not null)
        {
            return TaskResult<AssistantAgentReplay>.Failed(NhAssistantErrorCodes.ReplayActive, "The variant already has a replay that runs.");
        }

        var replay = new AssistantAgentReplay
        {
            Id = Guid.NewGuid(),
            VariantAgentId = variantId,
            SourceAgentId = sourceId,
            From = from,
            To = to,
            MaxRuns = maxRuns,
            Status = NhAssistantReplayStatuses.Running,
            RequestedByActorId = actorId,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await store.AddReplayAsync(replay, cancellationToken);
        var enqueued = await services.GetRequiredService<INhBackgroundOperationService>().EnqueueAsync(
            new NhAssistantReplayOperationRequest(replay.Id),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerAgentKey = NhAssistantProposalInvalidation.SystemActorId,
                IdempotencyKey = replay.Id.ToString("N"),
                ConcurrencyKey = "agent-replay:" + variantId,
                ConflictBehavior = NhBackgroundOperationConflictBehavior.Queue
            },
            cancellationToken);
        if (enqueued is not { Success: true })
        {
            logger.LogWarning("The replay {ReplayId} into agent {AgentId} could not be queued.", replay.Id, variantId);
            replay.Status = NhAssistantReplayStatuses.Stopped;
            replay.StopReason = NhAssistantRunStore.OrphanedRunCode;
            replay.EndedAt = DateTimeOffset.UtcNow;
            await store.TrySaveReplayAsync(replay, CancellationToken.None);
            return TaskResult<AssistantAgentReplay>.Failed(NhAssistantRunStore.OrphanedRunCode, "The replay could not be queued.");
        }

        await events.ReplayStartedAsync(replay.Id, variantId, sourceId, actorId, CancellationToken.None);
        return TaskResult<AssistantAgentReplay>.Succeeded(replay);
    }

    public static NhAssistantReplayDto ToDto(AssistantAgentReplay replay)
    {
        return new NhAssistantReplayDto(
            replay.Id,
            replay.VariantAgentId,
            replay.SourceAgentId,
            replay.From,
            replay.To,
            replay.MaxRuns,
            replay.Status,
            replay.StopReason,
            replay.RunsReplayed,
            replay.RunsSkipped,
            replay.CreatedAt,
            replay.EndedAt);
    }
}

/// <summary>
/// Runs a replay: it fires the recorded runs of the period into the variant, oldest first, as many
/// at a time as the variant may run, and continues after they started until the period or the
/// replay's maximum is done. The variant's state, run limit per subject and daily budget stop it.
/// </summary>
internal sealed class NhAssistantReplayOperation(
    NhAssistantRegistrationState registration,
    NhAssistantEvaluationStore store,
    NhAssistantRunStore runs,
    NhAssistantAgentTriggers triggers,
    NhAssistantShadowModes shadowModes,
    ILogger<NhAssistantReplayOperation> logger) : INhBackgroundOperationHandler<NhAssistantReplayOperationRequest>
{
    internal static readonly TimeSpan ContinueAfter = TimeSpan.FromSeconds(5);

    public async Task<TaskResult> ExecuteAsync(
        NhAssistantReplayOperationRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        var replay = await store.FindReplayAsync(request.ReplayId, cancellationToken);
        if (replay is not { Status: NhAssistantReplayStatuses.Running })
        {
            return TaskResult.Succeeded();
        }
        if (!registration.AgentRuns.ContainsKey(replay.VariantAgentId)
            || !await shadowModes.IsAgentShadowAsync(replay.VariantAgentId, cancellationToken))
        {
            await EndAsync(replay, NhAssistantReplayStatuses.Stopped, NhAssistantErrorCodes.ReplayNotShadow);
            return TaskResult.Succeeded();
        }

        var options = registration.AgentRuns[replay.VariantAgentId];
        if (await triggers.RefusalOfAsync(replay.VariantAgentId, refusePaused: true, cancellationToken, refuseOff: true) is { } refusal)
        {
            await EndAsync(replay, NhAssistantReplayStatuses.Stopped, refusal);
            return TaskResult.Succeeded();
        }

        var slots = options.MaxConcurrentRuns - await runs.CountActiveRunsAsync(replay.VariantAgentId, cancellationToken);
        var remaining = replay.MaxRuns - replay.RunsReplayed;
        if (remaining <= 0)
        {
            await EndAsync(replay, NhAssistantReplayStatuses.Completed, null);
            return TaskResult.Succeeded();
        }
        if (slots <= 0)
        {
            // The variant's runs use every slot; continue once some of them ended.
            return NhBackgroundOperationRetryResult.Retry("assistant-replay-busy", "assistant.replay-busy", ContinueAfter);
        }

        var batch = await store.GetReplaySourceRunsAsync(replay, Math.Min(slots, remaining), cancellationToken);
        if (batch.Count == 0)
        {
            await EndAsync(replay, NhAssistantReplayStatuses.Completed, null);
            return TaskResult.Succeeded();
        }

        var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        foreach (var (run, thread) in batch)
        {
            if (options.DailyBudget is { } budget)
            {
                var usage = await runs.GetAgentUsageSinceAsync(replay.VariantAgentId, thread.ScopeKey, dayStart, cancellationToken);
                if (usage.ToolCalls >= budget.ToolCalls || usage.Tokens >= budget.Tokens)
                {
                    await EndAsync(replay, NhAssistantReplayStatuses.Stopped, NhAssistantErrorCodes.BudgetExhausted);
                    return TaskResult.Succeeded();
                }
            }

            var inputs = await store.GetClaimedInputsAsync(run.Id, cancellationToken);
            if (inputs.Count == 0)
            {
                replay.RunsSkipped++;
            }
            else
            {
                var receipt = await triggers.ReplayAsync(replay.VariantAgentId, run, thread, inputs, cancellationToken);
                switch (receipt.Outcome)
                {
                    case NhAssistantTriggerOutcomes.Started
                        or NhAssistantTriggerOutcomes.Queued
                        or NhAssistantTriggerOutcomes.Deduplicated:
                        replay.RunsReplayed++;
                        break;
                    case NhAssistantTriggerOutcomes.Rejected
                        when receipt.ReasonCode is NhAssistantErrorCodes.SubjectNotFound or NhAssistantErrorCodes.NoAccountableOwner:
                        replay.RunsSkipped++;
                        break;
                    default:
                        // The run limit per subject, a stopped skill or another refusal ends the replay here.
                        await EndAsync(replay, NhAssistantReplayStatuses.Stopped, receipt.ReasonCode ?? receipt.Outcome);
                        return TaskResult.Succeeded();
                }
            }

            replay.CursorCreatedAt = run.CreatedAt;
            replay.CursorRunId = run.Id;
            if (!await store.TrySaveReplayAsync(replay, CancellationToken.None))
            {
                logger.LogWarning("Replay {ReplayId} was changed by another worker; this one stops.", replay.Id);
                return TaskResult.Succeeded();
            }
        }

        if (replay.RunsReplayed >= replay.MaxRuns)
        {
            await EndAsync(replay, NhAssistantReplayStatuses.Completed, null);
            return TaskResult.Succeeded();
        }

        // More runs wait; the next execution continues once these took their slots.
        return NhBackgroundOperationRetryResult.Retry("assistant-replay-continues", "assistant.replay-continues", ContinueAfter);
    }

    private async Task EndAsync(AssistantAgentReplay replay, string status, string? reason)
    {
        replay.Status = status;
        replay.StopReason = reason;
        replay.EndedAt = DateTimeOffset.UtcNow;
        await store.TrySaveReplayAsync(replay, CancellationToken.None);
        logger.LogInformation(
            "Replay {ReplayId} into agent {AgentId} ended {Status} after {Runs} runs ({Reason}).",
            replay.Id,
            replay.VariantAgentId,
            status,
            replay.RunsReplayed,
            reason ?? "done");
    }
}
