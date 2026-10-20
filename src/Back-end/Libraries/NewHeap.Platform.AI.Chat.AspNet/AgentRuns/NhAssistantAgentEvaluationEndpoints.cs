using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The evaluation of shadow variants for administrators: the comparison with the live agent, its
/// export as JSON Lines and replays of recorded runs.
/// </summary>
internal static class NhAssistantAgentEvaluationEndpoints
{
    private const int MaxListedReplays = 20;
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(7);

    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/evaluation", GetAsync)
            .WithName("NhAssistantAdminGetAgentEvaluation")
            .WithSummary("Compare a shadow variant with its live agent")
            .WithDescription("Pairs the runs of the agent and the variant that handled the same event or that a replay repeated, in the period (seven days by default, at most 92), and returns per tool the agreed, live-only and variant-only proposals with what people decided about the live ones, the cost of both and how both routed. Counts, identifiers and links only.")
            .Produces<NhAssistantEvaluationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapGet("agents/{id}/evaluation/export", ExportAsync)
            .WithName("NhAssistantAdminExportAgentEvaluation")
            .WithSummary("Export the comparison of a shadow variant with its live agent")
            .WithDescription("Streams the paired runs of the comparison as JSON Lines: identifiers, statuses, usage and the proposals' tools, statuses and argument hashes, never messages or arguments. The export is audited.")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-ndjson")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPost("agents/{id}/replay", StartReplayAsync)
            .WithName("NhAssistantAdminStartAgentReplay")
            .WithSummary("Replay recorded runs into a shadow variant")
            .WithDescription("Fires the recorded inputs of another agent's runs in the period into the shadow variant, oldest first, against today's state of each subject: at most 500 runs and one active replay per variant. The variant's state, run limits and budget stop it. Audited.")
            .Produces<NhAssistantReplayDto>(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/replays", ListReplaysAsync)
            .WithName("NhAssistantAdminListAgentReplays")
            .WithSummary("List the replays into a shadow variant")
            .WithDescription("Returns the latest replays into the variant, newest first, with how far each got and why it stopped.")
            .Produces<NhAssistantReplayListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext httpContext,
        NhAssistantEvaluationService evaluation,
        string? variant,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var request = Read(id, variant, from, to, evaluation);
        if (request.Error is not null)
        {
            return request.Error;
        }

        var report = await evaluation.CompareAsync(id, request.Variant!, request.From, request.To, httpContext.RequestAborted);
        return TypedResults.Json(report, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantEvaluationDto);
    }

    private static async Task<IResult> ExportAsync(
        string id,
        HttpContext httpContext,
        NhAssistantEvaluationService evaluation,
        NhAssistantAgentEvents events,
        string? variant,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var request = Read(id, variant, from, to, evaluation);
        if (request.Error is not null)
        {
            return request.Error;
        }
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var records = await evaluation.ExportAsync(id, request.Variant!, request.From, request.To, httpContext.RequestAborted);
        await events.EvaluationExportedAsync(id, request.Variant!, caller.Data!.ActorId, httpContext.RequestAborted);
        return TypedResults.Stream(
            async stream =>
            {
                var newLine = "\n"u8.ToArray();
                foreach (var record in records)
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        record,
                        NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantEvaluationExportRecord,
                        httpContext.RequestAborted);
                    await stream.WriteAsync(newLine, httpContext.RequestAborted);
                }
            },
            "application/x-ndjson",
            "agent-evaluation-" + id + "-" + request.Variant + ".jsonl");
    }

    private static async Task<IResult> StartReplayAsync(
        string id,
        HttpContext httpContext,
        NhAssistantReplayService replays)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var request = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantReplayRequest);
        if (request is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var started = await replays.StartAsync(id, request, caller.Data!.ActorId, httpContext.RequestAborted);
        if (!started.Success)
        {
            return NhAssistantHttpErrors.Failure(started);
        }

        return TypedResults.Json(
            NhAssistantReplayService.ToDto(started.Data!),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantReplayDto,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListReplaysAsync(
        string id,
        HttpContext httpContext,
        NhAssistantRegistrationState registration,
        NhAssistantEvaluationStore store)
    {
        if (!registration.AgentRuns.ContainsKey(id))
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
        }

        var replays = await store.ListReplaysAsync(id, MaxListedReplays, httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantReplayListDto(replays.Select(NhAssistantReplayService.ToDto).ToArray()),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantReplayListDto);
    }

    /// <summary>
    /// Validates the comparison: the agent is configured, the variant is a shadow agent, and the
    /// period ends after it starts and spans at most 92 days.
    /// </summary>
    private static (string? Variant, DateTimeOffset From, DateTimeOffset To, IResult? Error) Read(
        string id,
        string? variant,
        DateTimeOffset? from,
        DateTimeOffset? to,
        NhAssistantEvaluationService evaluation)
    {
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end - DefaultPeriod).ToUniversalTime();
        if (string.IsNullOrWhiteSpace(variant) || start >= end || end - start > NhAssistantEvaluationLimits.MaxPeriod)
        {
            return (null, start, end, NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid));
        }
        if (!evaluation.IsConfigured(id))
        {
            return (null, start, end, NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound));
        }
        if (string.Equals(id, variant, StringComparison.Ordinal) || !evaluation.IsVariant(variant))
        {
            return (null, start, end, NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.EvaluationNotVariant));
        }

        return (variant, start, end, null);
    }
}
