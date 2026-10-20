using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Groups and boards over HTTP (K15, K16): <c>GET boards</c>, <c>GET boards/{group}</c> and
/// <c>POST boards/{group}/visit</c> for people, and <c>PUT admin/agents/{id}/groups</c> for
/// administrators.
/// </summary>
internal static class NhAssistantBoardEndpoints
{
    private const string ScopeDescription = " divisionId (optional) opens the board of one division the caller may work in, where agents that work per division show their work for that division; without it the board is the whole application's. An invalid division id answers 400 assistant-board-scope-invalid.";

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("boards", ListAsync)
            .WithName("NhAssistantListBoards")
            .WithSummary("List the boards of agent groups")
            .WithDescription("Returns the groups of agents the application registered whose board shows the caller at least one agent, with their names in the request's language, ordered by name. An agent that works on the whole application shows to whoever passes its view policy, by default its required policy; another agent to whoever may use it.")
            .Produces<NhAssistantBoardListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        group.MapGet("boards/{groupId}", GetAsync)
            .WithName("NhAssistantGetBoard")
            .WithSummary("Read the board of an agent group")
            .WithDescription("Returns the group's agents the caller sees, each with what it works on, its latest run for the whole application with the first 300 characters of its result, its open proposals, whether the caller may run it now (the agent's run policy, by default the administration policy, and its view policy) and whether its latest run ended or a proposal was made after the caller's last visit of this board in this scope. An unknown group, or a group of which the caller sees no agent, answers 404 assistant-board-not-found." + ScopeDescription)
            .Produces<NhAssistantBoardDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        group.MapPost("boards/{groupId}/visit", VisitAsync)
            .WithName("NhAssistantVisitBoard")
            .WithSummary("Record a visit of a board")
            .WithDescription("Records that the caller looked at the group's board in its scope now, so the board marks what is new after this visit. Only the person, the group, the scope and the time are stored. A board the caller may not open answers 404 assistant-board-not-found." + ScopeDescription)
            .Produces<NhAssistantBoardVisitDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Maps group membership into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapPut("agents/{id}/groups", SetGroupsAsync)
            .WithName("NhAssistantAdminSetAgentGroups")
            .WithSummary("Put an agent in groups")
            .WithDescription("Puts the agent, in code or created in the administration, in exactly the given groups registered with AddAgentGroup and out of the others. The choice wins over the agent's InGroup in code; a choice that equals code lets the code decide again. groups is required; an unknown group or more than 100 answer 400 with a field error on groups, an unknown agent 404, a shadow variant, which boards never show, 409 assistant-shadow-variant, and a save that collides with another administrator's save of the same agent 409 assistant-version-conflict.")
            .Produces<NhAssistantAgentGroupMembershipDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListAsync(HttpContext httpContext, NhAssistantAgentBoards boards)
    {
        var reader = await ReaderAsync(httpContext);
        if (reader is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var list = await boards.ListAsync(reader, httpContext.RequestAborted);
        return TypedResults.Json(list, NhAssistantAgentBoardJsonSerializerContext.Default.NhAssistantBoardListDto);
    }

    private static async Task<IResult> GetAsync(
        string groupId,
        HttpContext httpContext,
        NhAssistantAgentBoards boards,
        string? divisionId)
    {
        var reader = await ReaderAsync(httpContext);
        if (reader is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var board = await boards.GetAsync(groupId, divisionId, reader, httpContext.RequestAborted);
        return board.Success
            ? TypedResults.Json(board.Data!, NhAssistantAgentBoardJsonSerializerContext.Default.NhAssistantBoardDto)
            : Refusal(board);
    }

    private static async Task<IResult> VisitAsync(
        string groupId,
        HttpContext httpContext,
        NhAssistantAgentBoards boards,
        string? divisionId)
    {
        var reader = await ReaderAsync(httpContext);
        if (reader is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.ContextUnavailable();
        }

        var visit = await boards.VisitAsync(groupId, divisionId, reader, httpContext.RequestAborted);
        return visit.Success
            ? TypedResults.Json(visit.Data!, NhAssistantAgentBoardJsonSerializerContext.Default.NhAssistantBoardVisitDto)
            : Refusal(visit);
    }

    private static async Task<IResult> SetGroupsAsync(string id, HttpContext httpContext, INhAssistantAgentGroups groups)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantEndpointRouteBuilderExtensions.ReadBodyAsync(
            httpContext,
            NhAssistantAgentBoardJsonSerializerContext.Default.NhAssistantAgentGroupsRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.Groups is null)
        {
            return NhAssistantAdminEndpoints.MissingField("groups");
        }

        var result = await groups.SetMembershipAsync(id, body.Groups, actor, httpContext.RequestAborted);
        return result.Success
            ? TypedResults.Json(result.Data!, NhAssistantAgentBoardJsonSerializerContext.Default.NhAssistantAgentGroupMembershipDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    /// <summary>
    /// A board the caller may not open answers 404 and a malformed scope 400, as every failure maps
    /// with the assistant's one status per code.
    /// </summary>
    private static IResult Refusal(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return NhAssistantHttpErrors.Failure(result);
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
