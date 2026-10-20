using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The results and history of agent runs over HTTP: <c>GET runs</c> and <c>GET runs/{id}</c> for
/// people (K14), the files of a run, and <c>GET admin/agents/{id}/runs</c>, the runs view of an agent
/// for administrators.
/// </summary>
internal static class NhAssistantRunHistoryEndpoints
{
    private const string FilterDescription = " Filters: agentId, subjectType (nh-system for the whole application), subjectId (with subjectType), status (queued, running, waiting-for-approval, completed, failed or cancelled), from and to (when the run was created, from inclusive, to exclusive), page (from 1) and itemsPerPage (1 to 50, default 20); an invalid filter answers 400 assistant-run-filter-invalid with the invalid parameters in errors (page, itemsPerPage, agentId, subjectType, subjectId, status or to).";

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("runs", ListAsync)
            .WithName("NhAssistantListRunHistory")
            .WithSummary("List the agent runs the caller may see")
            .WithDescription("Returns the runs of agents the caller may see, newest first, with the subject, trigger, status, usage, times and the first 300 characters of each result: runs on an object to whoever may read the object (or was invited to the agent's thread there), runs for the whole application to whoever passes the agent's view policy, by default its required policy. Shadow work is for administrators only. The history considers the 100 agent threads with the most recent matching runs; limited says older threads were left out, which a narrower filter reaches." + FilterDescription)
            .Produces<NhAssistantRunHistoryListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        group.MapGet("runs/{runId:guid}", GetAsync)
            .WithName("NhAssistantGetRunResult")
            .WithSummary("Read one agent run with its result")
            .WithDescription("Returns one run with its whole answer as Markdown (at most 20,000 characters, model output to render only with a safe Markdown renderer), the summary it ended with, its proposals in its own thread and those it placed at objects the caller may read, how many it placed elsewhere, and the files it saved. People see a run as GET runs decides; administrators every run. A run the caller may not see answers 404 like an unknown one.")
            .Produces<NhAssistantRunResultDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapGet("runs/{runId:guid}/artifacts/{artifactId:guid}/content", GetArtifactContentAsync)
            .WithName("NhAssistantGetRunArtifactContent")
            .WithSummary("Download a file a run saved")
            .WithDescription("Returns the content of one file version the run saved, for a caller who may see the run. A file the agent's own grant read that the caller could not have read answers 403 assistant-subject-access-denied.")
            .Produces(StatusCodes.Status200OK)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Maps the runs view into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/runs", ListAgentRunsAsync)
            .WithName("NhAssistantAdminListAgentRuns")
            .WithSummary("List every run of an agent")
            .WithDescription("Returns every run of the agent, live and shadow (marked shadow), newest first, with the subject, trigger, status, usage, times and the first 300 characters of each result, for the agent's runs view. Read a run's whole result with GET runs/{id}." + FilterDescription + " An agentId filter is ignored here.")
            .Produces<NhAssistantRunHistoryListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        NhAssistantRunHistory history,
        string? agentId,
        string? subjectType,
        string? subjectId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? itemsPerPage)
    {
        var reader = await ReaderAsync(httpContext);
        if (reader is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }
        var query = NhAssistantRunHistory.ReadQuery(agentId, subjectType, subjectId, status, from, to, page, itemsPerPage);
        if (!query.Success)
        {
            return NhAssistantHttpErrors.Failure(query);
        }

        var list = await history.ListAsync(query.Data!, reader, httpContext.RequestAborted);
        return TypedResults.Json(list, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantRunHistoryListDto);
    }

    private static async Task<IResult> GetAsync(
        Guid runId,
        HttpContext httpContext,
        NhAssistantRunHistory history)
    {
        var reader = await ReaderAsync(httpContext);
        if (reader is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var read = await history.ReadAsync(runId, reader, httpContext, httpContext.RequestAborted);
        return read.Success
            ? TypedResults.Json(read.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantRunResultDto)
            : NhAssistantHttpErrors.Failure(read);
    }

    private static async Task<IResult> GetArtifactContentAsync(
        Guid runId,
        Guid artifactId,
        HttpContext httpContext,
        NhAssistantRunHistory history)
    {
        var reader = await ReaderAsync(httpContext);
        if (reader is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var found = await history.FindArtifactAsync(runId, artifactId, reader, httpContext, httpContext.RequestAborted);
        if (!found.Success)
        {
            return NhAssistantHttpErrors.Failure(found);
        }

        var artifact = found.Data!;
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(
            artifact.Content,
            artifact.ContentType,
            artifact.Name + "-v" + artifact.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<IResult> ListAgentRunsAsync(
        string id,
        HttpContext httpContext,
        NhAssistantRunHistory history,
        string? subjectType,
        string? subjectId,
        string? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? itemsPerPage)
    {
        // The filter is checked before the agent, so an invalid filter answers 400 for any agent.
        var query = NhAssistantRunHistory.ReadQuery(null, subjectType, subjectId, status, from, to, page, itemsPerPage);
        if (!query.Success)
        {
            return NhAssistantHttpErrors.Failure(query);
        }

        var list = await history.ListForAgentAsync(id, query.Data!, httpContext.RequestAborted);
        return list.Success
            ? TypedResults.Json(list.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantRunHistoryListDto)
            : NhAssistantHttpErrors.Failure(list);
    }

    private static async Task<NhAssistantRunReader?> ReaderAsync(HttpContext httpContext)
    {
        var caller = await NhAssistantEndpointRouteBuilderExtensions.ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return null;
        }

        return new NhAssistantRunReader(
            caller.Data!.ActorId,
            caller.Data.TenantId,
            httpContext.User,
            await NhAssistantEndpointRouteBuilderExtensions.IsAdministratorAsync(httpContext));
    }
}
