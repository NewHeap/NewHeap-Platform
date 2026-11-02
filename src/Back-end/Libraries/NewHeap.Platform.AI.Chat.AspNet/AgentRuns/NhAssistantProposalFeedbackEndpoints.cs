using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Feedback on closed agent proposals: people say what was wrong, and administrators read the
/// rejection rates, the categories and the texts per agent, tool and agent version, or export them
/// as JSON Lines for their own analysis.
/// </summary>
internal static class NhAssistantProposalFeedbackEndpoints
{
    private const int MaxBulkFeedback = 50;
    private const int DefaultExportLimit = 10_000;
    private const int MaxExportLimit = 50_000;
    private const int ExportBatchSize = 500;
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPut("approvals/{approvalId:guid}/feedback", GiveAsync)
            .WithName("NhAssistantGiveProposalFeedback")
            .WithSummary("Say what was wrong with an agent proposal")
            .WithDescription("Stores or replaces the caller's feedback on a rejected, expired, withdrawn or stale proposal of an agent thread: a category and an optional text. The person who decided the proposal and the subject's approvers may give feedback.")
            .Produces<NhAssistantProposalFeedbackDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        group.MapPost("approvals/feedback", GiveManyAsync)
            .WithName("NhAssistantGiveProposalsFeedback")
            .WithSummary("Say what was wrong with several agent proposals")
            .WithDescription("Stores the same feedback on proposals that were rejected together; each proposal is checked on its own and the records are marked as bulk feedback.")
            .Produces<NhAssistantBulkProposalFeedbackDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// Maps the report endpoints into the administration group, which already requires the admin
    /// policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/feedback/summary", GetSummaryAsync)
            .WithName("NhAssistantAdminGetAgentFeedbackSummary")
            .WithSummary("Summarize the feedback on an agent's proposals")
            .WithDescription("Returns, per tool and agent version, how many proposals made in the period were approved, rejected, expired or withdrawn, the rejection rate, how often people chose each feedback category, and decisionTime: the median and the 90th percentile of the time from a proposal to a person's approval or rejection, in whole seconds, from at most the newest 1,000 decisions per row (sampled says when there were more). The period defaults to the last 30 days and spans at most 366 days.")
            .Produces<NhAssistantFeedbackSummaryDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapGet("proposal-kinds", GetProposalKindsAsync)
            .WithName("NhAssistantAdminGetProposalKinds")
            .WithSummary("Compare the kinds of action agents propose")
            .WithDescription("Returns per kind of action (tool) over all agents, for proposals made in the period: the counts per outcome, the approval and rejection rates, decisionTime (the median and the 90th percentile of the time to a person's approval or rejection, in whole seconds, from at most the newest 1,000 decisions per agent) and the name the proposal cards show; and per agent the same numbers with candidate: whether they meet every threshold for running the kind without approval (thresholds: at least minimumDecisions decisions, at least minimumApprovalRate approved, no rejection within quietPeriodDays before the period's end and at most maximumStaleOrFailedAfterApproval approved proposals that closed as stale when they ran or failed), with unmetThresholds naming the codes too-few-decisions, approval-rate-too-low, recent-rejection and stale-or-failed-after-approval. The thresholds come from NewHeap:AI:Assistant:RoutineCandidates and are returned with the view. A candidate is a suggestion only: nothing about deciding changes. Filters: agentId and toolId. The period defaults to the last 30 days and spans at most 366 days; an invalid period or filter answers 400.")
            .Produces<NhAssistantProposalKindsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
        admin.MapGet("agents/{id}/feedback", ListAsync)
            .WithName("NhAssistantAdminListAgentFeedback")
            .WithSummary("List the feedback on an agent's proposals")
            .WithDescription("Returns the feedback on proposals made in the period, newest change first, with the proposal as approvers read it. Filter by scope key, tool, category and agent version.")
            .Produces<NhAssistantFeedbackListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapGet("agents/{id}/feedback/export", ExportAsync)
            .WithName("NhAssistantAdminExportAgentFeedback")
            .WithSummary("Export the feedback on an agent's proposals")
            .WithDescription("Streams the feedback on proposals made in the period as JSON Lines, oldest first, with the tool input of each proposal, up to limit records (default 10,000, at most 50,000). The file holds subject data. The export is audited.")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-ndjson")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GiveAsync(
        Guid approvalId,
        HttpContext httpContext,
        NhAssistantProposalFeedbackService feedback)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalFeedbackRequest);
        if (request is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.FeedbackInvalid);
        }

        var given = await feedback.GiveAsync(
            approvalId,
            caller.Data!.ActorId,
            request.Category,
            request.Text,
            bulk: false,
            httpContext.RequestAborted,
            caller.Data.TenantId);
        if (!given.Success)
        {
            return NhAssistantHttpErrors.Failure(given);
        }

        return TypedResults.Json(
            NhAssistantAgentRunDtoMapper.ToDto(given.Data!),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalFeedbackDto);
    }

    private static async Task<IResult> GiveManyAsync(
        HttpContext httpContext,
        NhAssistantProposalFeedbackService feedback)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantBulkProposalFeedbackRequest);
        if (request?.ApprovalIds is not { Count: > 0 and <= MaxBulkFeedback } approvalIds)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.FeedbackInvalid);
        }

        var results = new List<NhAssistantFeedbackOutcomeDto>(approvalIds.Count);
        foreach (var approvalId in approvalIds.Distinct())
        {
            var given = await feedback.GiveAsync(
                approvalId,
                caller.Data!.ActorId,
                request.Category,
                request.Text,
                bulk: true,
                httpContext.RequestAborted,
                caller.Data.TenantId);
            results.Add(new NhAssistantFeedbackOutcomeDto(approvalId, given.Success, given.Success ? null : NhAssistantResults.CodeOf(given, NhAssistantErrorCodes.TurnFailed)));
        }

        return TypedResults.Json(
            new NhAssistantBulkProposalFeedbackDto(results),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantBulkProposalFeedbackDto);
    }

    private static async Task<IResult> GetSummaryAsync(
        string id,
        HttpContext httpContext,
        NhAssistantAgentCatalog agents,
        NhAssistantProposalFeedbackStore store,
        NhAssistantDecisionNumbersStore numbers,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? scopeKey,
        string? toolId,
        int? agentVersion)
    {
        var filter = ReadFilter(from, to, scopeKey, toolId, category: null, agentVersion);
        if (filter is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }
        if (await agents.FindAsync(id, includeDisabled: true, httpContext.RequestAborted) is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
        }

        var aggregates = await store.GetAggregatesAsync(id, filter, httpContext.RequestAborted);
        var decisionTimes = await numbers.GetDecisionTimesAsync(
            id,
            filter,
            NhAssistantDecisionTimes.SamplePerGroup,
            httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantFeedbackSummaryDto(id, filter.From, filter.To, Summarize(aggregates, decisionTimes)),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantFeedbackSummaryDto);
    }

    private static async Task<IResult> GetProposalKindsAsync(
        HttpContext httpContext,
        NhAssistantProposalKindReport report,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? agentId,
        string? toolId)
    {
        var filter = ReadFilter(from, to, scopeKey: null, toolId, category: null, agentVersion: null);
        // An agent id has at most 128 characters; an unknown agent lists nothing.
        if (filter is null || agentId is { Length: 0 or > 128 })
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var kinds = await report.GetAsync(
            new NhAssistantProposalKindFilter(filter.From, filter.To, agentId, toolId),
            httpContext.RequestAborted);
        return TypedResults.Json(kinds, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantProposalKindsDto);
    }

    private static async Task<IResult> ListAsync(
        string id,
        HttpContext httpContext,
        NhAssistantAgentCatalog agents,
        NhAssistantProposalFeedbackStore store,
        NhAssistantRunStore runs,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? scopeKey,
        string? toolId,
        string? category,
        int? agentVersion,
        int? page,
        int? itemsPerPage)
    {
        var filter = ReadFilter(from, to, scopeKey, toolId, category, agentVersion);
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        if (filter is null || pageNumber < 1 || size is < 1 or > 100)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }
        if (await agents.FindAsync(id, includeDisabled: true, httpContext.RequestAborted) is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
        }

        var (items, total) = await store.ListAsync(id, filter, pageNumber, size, httpContext.RequestAborted);
        var names = await runs.GetDisplayNamesAsync(
            items.Select(item => item.Feedback.AuthorActorId),
            httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantFeedbackListDto(
                items
                    .Select(item => NhAssistantAgentRunDtoMapper.ToDto(item) with
                    {
                        AuthorDisplayName = names.GetValueOrDefault(item.Feedback.AuthorActorId)
                    })
                    .ToArray(),
                total),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantFeedbackListDto);
    }

    private static async Task<IResult> ExportAsync(
        string id,
        HttpContext httpContext,
        NhAssistantAgentCatalog agents,
        NhAssistantProposalFeedbackStore store,
        NhAssistantAgentEvents events,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? scopeKey,
        string? toolId,
        string? category,
        int? agentVersion,
        int? limit)
    {
        var filter = ReadFilter(from, to, scopeKey, toolId, category, agentVersion);
        var maximum = limit ?? DefaultExportLimit;
        if (filter is null || maximum is < 1 or > MaxExportLimit)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        if (await agents.FindAsync(id, includeDisabled: true, httpContext.RequestAborted) is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
        }

        await events.FeedbackExportedAsync(id, caller.Data!.ActorId, httpContext.RequestAborted);
        return TypedResults.Stream(
            async stream =>
            {
                var newLine = "\n"u8.ToArray();
                await foreach (var row in store.ExportAsync(id, filter, maximum, ExportBatchSize, httpContext.RequestAborted))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        ToExportRecord(row),
                        NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantFeedbackExportRecord,
                        httpContext.RequestAborted);
                    await stream.WriteAsync(newLine, httpContext.RequestAborted);
                }
            },
            "application/x-ndjson",
            "agent-feedback-" + id + ".jsonl");
    }

    private static NhAssistantFeedbackExportRecord ToExportRecord(NhAssistantFeedbackRow row)
    {
        var proposal = Governance.NhAssistantProposalSerializer.Deserialize(row.ProposalJson!);
        return new NhAssistantFeedbackExportRecord(
            NhAssistantAgentRunDtoMapper.ToDto(row),
            proposal.ToolContractHash,
            proposal.PromptHash,
            proposal.Arguments);
    }

    /// <summary>
    /// Validates the report filter; the period defaults to the last 30 days, ends after it starts
    /// and spans at most 366 days.
    /// </summary>
    private static NhAssistantFeedbackFilter? ReadFilter(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? scopeKey,
        string? toolId,
        string? category,
        int? agentVersion)
    {
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end - DefaultPeriod).ToUniversalTime();
        if (start >= end
            || end - start > MaxPeriod
            || scopeKey is { Length: 0 or > 128 }
            || toolId is { Length: 0 or > 256 }
            || (category is not null && !NhAssistantProposalFeedbackCategories.IsKnown(category))
            || agentVersion is < 0)
        {
            return null;
        }

        return new NhAssistantFeedbackFilter(start, end, scopeKey, toolId, category, agentVersion);
    }

    private static IReadOnlyList<NhAssistantFeedbackSummaryRowDto> Summarize(
        NhAssistantFeedbackAggregates aggregates,
        IReadOnlyList<NhAssistantDecisionTimeRow> decisionTimes)
    {
        var keys = aggregates.Statuses.Select(row => (row.ToolId, row.AgentVersion))
            .Concat(aggregates.Categories.Select(row => (row.ToolId, row.AgentVersion)))
            .Distinct()
            .OrderBy(key => key.ToolId, StringComparer.Ordinal)
            .ThenByDescending(key => key.AgentVersion ?? int.MinValue);
        var rows = new List<NhAssistantFeedbackSummaryRowDto>();
        foreach (var (toolId, agentVersion) in keys)
        {
            var statuses = aggregates.Statuses
                .Where(row => row.ToolId == toolId && row.AgentVersion == agentVersion)
                .ToDictionary(row => row.Status, row => row.Count, StringComparer.Ordinal);
            int Count(params string[] names)
            {
                return names.Sum(name => statuses.GetValueOrDefault(name));
            }

            var invalidations = aggregates.Invalidations
                .Where(row => row.ToolId == toolId && row.AgentVersion == agentVersion)
                .ToArray();
            var invalidated = invalidations.Where(row => !row.AfterApproval).Sum(row => row.Count);
            var staleAfterApproval = Count(NhAssistantApprovalStatuses.Stale) - invalidated;
            var approved = Count(
                NhAssistantApprovalStatuses.Approved,
                NhAssistantApprovalStatuses.Executing,
                NhAssistantApprovalStatuses.AwaitingApplication,
                NhAssistantApprovalStatuses.Executed,
                NhAssistantApprovalStatuses.ExecutionFailed,
                NhAssistantApprovalStatuses.ExecutionUnknown,
                NhAssistantApprovalStatuses.Resolved) + staleAfterApproval;
            var rejected = Count(NhAssistantApprovalStatuses.Rejected);
            var reach = aggregates.Reach.FirstOrDefault(row => row.ToolId == toolId && row.AgentVersion == agentVersion);
            var rejectedWithFeedback = aggregates.RejectedWithFeedback
                .FirstOrDefault(row => row.ToolId == toolId && row.AgentVersion == agentVersion);
            var categories = aggregates.Categories
                .Where(row => row.ToolId == toolId && row.AgentVersion == agentVersion)
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Category, StringComparer.Ordinal)
                .Select(row => new NhAssistantFeedbackCategoryCountDto(
                    row.Category,
                    NhAssistantAgentRunDtoMapper.GroupOf(row.Category),
                    row.Count))
                .ToArray();
            var (sample, sampled) = NhAssistantDecisionTimes.SampleOf(decisionTimes
                .Where(row => row.ToolId == toolId && row.AgentVersion == agentVersion)
                .ToList());

            rows.Add(new NhAssistantFeedbackSummaryRowDto(
                toolId,
                agentVersion,
                Count(NhAssistantApprovalStatuses.Pending),
                approved,
                rejected,
                Count(NhAssistantApprovalStatuses.Expired),
                Count(NhAssistantApprovalStatuses.Withdrawn),
                staleAfterApproval,
                Count(NhAssistantApprovalStatuses.ExecutionFailed),
                approved + rejected == 0 ? null : (double)rejected / (approved + rejected),
                rejectedWithFeedback?.Proposals ?? 0,
                reach?.Proposals ?? 0,
                reach?.Authors ?? 0,
                categories)
            {
                Invalidated = invalidated,
                Invalidations = invalidations
                    .OrderByDescending(row => row.Count)
                    .ThenBy(row => row.Kind, StringComparer.Ordinal)
                    .ThenBy(row => row.ReasonCode, StringComparer.Ordinal)
                    .Select(row => new NhAssistantInvalidationCountDto(row.Kind, row.ReasonCode, row.AfterApproval, row.Count))
                    .ToArray(),
                DecisionTime = NhAssistantDecisionTimes.Of(sample, sampled)
            });
        }

        return rows;
    }
}
