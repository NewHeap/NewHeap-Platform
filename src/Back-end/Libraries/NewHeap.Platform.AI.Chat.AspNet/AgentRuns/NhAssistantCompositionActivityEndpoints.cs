using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The compositions and scripts an agent used (TC1): per call the tool calls it ran, in order, the
/// model rounds that saved and the size of the result returned to the model; per tool how often it was
/// called and what it saved. Content-free: tool ids, statuses, codes, sizes and times, never arguments
/// or results.
/// </summary>
internal static class NhAssistantCompositionActivityEndpoints
{
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/compositions/activity", GetAsync)
            .WithName("NhAssistantAdminGetAgentCompositionActivity")
            .WithSummary("Show what an agent's compositions and scripts ran")
            .WithDescription($"Returns the agent's composition and script calls started in the period, newest first and at most {NhAssistantCompositionActivity.MaxCalls}: per call the tool calls it ran in order with their status and code, the model rounds it saved (its tool calls minus one) and the size of the result returned to the model in characters; and per tool, over those calls, the calls, tool calls, rounds saved and result sizes. total counts every call of the period. The period defaults to the last 30 days and spans at most 366 days. Calls inside a consultation are not stored and not listed.")
            .Produces<NhAssistantCompositionActivityDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext httpContext,
        NhAssistantAgentCatalog agents,
        NhAssistantCompositionActivity activity,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var end = (to ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var start = (from ?? end - DefaultPeriod).ToUniversalTime();
        if (start >= end || end - start > MaxPeriod)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.MessageInvalid);
        }
        if (await agents.FindAsync(id, includeDisabled: true, httpContext.RequestAborted) is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(NhAssistantErrorCodes.AgentNotFound);
        }

        var read = await activity.GetAsync(id, start, end, httpContext.RequestAborted);
        return TypedResults.Json(
            new NhAssistantCompositionActivityDto(
                id,
                start,
                end,
                read.Total,
                read.Total > read.Calls.Count,
                read.Tools
                    .Select(tool => new NhAssistantCompositionToolUsageDto(
                        tool.ToolId,
                        KindOf(tool.Feature),
                        tool.Calls,
                        tool.ToolCalls,
                        tool.RoundsSaved,
                        tool.AverageResultCharacters,
                        tool.MaxResultCharacters))
                    .ToArray(),
                read.Calls
                    .Select(call => new NhAssistantCompositionCallDto(
                        call.InvocationId,
                        call.ConversationId,
                        call.ToolId,
                        KindOf(call.Feature),
                        call.Status,
                        call.ResultCode,
                        call.StartedAt,
                        call.Steps
                            .Select(step => new NhAssistantCompositionStepDto(step.Index, step.ToolId, step.Status, step.ResultCode))
                            .ToArray(),
                        call.RoundsSaved,
                        call.ResultCharacters))
                    .ToArray()),
            NhAssistantCompositionJsonSerializerContext.Default.NhAssistantCompositionActivityDto);
    }

    private static string KindOf(NhAiCompositionFeature feature)
    {
        return feature == NhAiCompositionFeature.Scripts ? "script" : "composition";
    }
}

/// <summary>
/// What an agent's compositions and scripts ran in a period (<c>GET admin/agents/{id}/compositions/activity</c>).
/// </summary>
/// <param name="Total">Every composition and script call of the period.</param>
/// <param name="Truncated">The period held more calls than <paramref name="Calls"/> lists; <paramref name="Tools"/> covers the listed ones.</param>
/// <param name="Tools">The use per composition and script tool over the listed calls.</param>
/// <param name="Calls">The newest calls of the period with their steps.</param>
public sealed record NhAssistantCompositionActivityDto(
    string AgentId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Total,
    bool Truncated,
    IReadOnlyList<NhAssistantCompositionToolUsageDto> Tools,
    IReadOnlyList<NhAssistantCompositionCallDto> Calls);

/// <param name="Kind"><c>composition</c> or <c>script</c>.</param>
/// <param name="ToolCalls">The tool calls the calls ran.</param>
/// <param name="RoundsSaved">The model rounds the calls saved: per call its tool calls minus one.</param>
/// <param name="AverageResultCharacters">The average size of the result returned to the model, in characters of JSON.</param>
public sealed record NhAssistantCompositionToolUsageDto(
    string ToolId,
    string Kind,
    int Calls,
    int ToolCalls,
    int RoundsSaved,
    int AverageResultCharacters,
    int MaxResultCharacters);

/// <param name="InvocationId">The call's tool invocation.</param>
/// <param name="ConversationId">The conversation or agent thread the call was made in.</param>
/// <param name="Kind"><c>composition</c> or <c>script</c>.</param>
/// <param name="Status">The call's tool call status, such as <c>succeeded</c> or <c>failed</c>.</param>
/// <param name="ResultCode">The code a failed call ended with, such as the code of the step that stopped it.</param>
/// <param name="Steps">The tool calls the call ran, in order.</param>
/// <param name="RoundsSaved">The model rounds the call saved: its tool calls minus one, at least 0.</param>
/// <param name="ResultCharacters">The size of the result returned to the model, in characters of JSON.</param>
public sealed record NhAssistantCompositionCallDto(
    Guid InvocationId,
    Guid ConversationId,
    string ToolId,
    string Kind,
    string Status,
    string? ResultCode,
    DateTimeOffset StartedAt,
    IReadOnlyList<NhAssistantCompositionStepDto> Steps,
    int RoundsSaved,
    int ResultCharacters);

/// <param name="Index">The step's position in its call, from 0.</param>
/// <param name="Status">The step's tool call status, such as <c>succeeded</c>, <c>awaiting-approval</c> or <c>not-executed</c>.</param>
public sealed record NhAssistantCompositionStepDto(int Index, string ToolId, string Status, string? ResultCode);

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantCompositionActivityDto))]
internal sealed partial class NhAssistantCompositionJsonSerializerContext : JsonSerializerContext;
