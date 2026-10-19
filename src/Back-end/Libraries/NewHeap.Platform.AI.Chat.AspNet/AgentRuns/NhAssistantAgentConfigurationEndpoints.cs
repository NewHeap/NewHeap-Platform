using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The run configuration of agents as data for administrators: the building blocks, the agent's
/// skills and triggers in code and as data, and the changes of data skills and data triggers.
/// </summary>
internal static class NhAssistantAgentConfigurationEndpoints
{
    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("run-catalog", GetCatalog)
            .WithName("NhAssistantAdminGetRunCatalog")
            .WithSummary("List the building blocks of agent runs")
            .WithDescription("Returns the trigger events, subject sets and rule handlers registered in code, the approval policies and the chat profiles that skills and triggers defined as data can name.")
            .Produces<NhAssistantRunCatalogDto>();
        admin.MapGet("agents/{id}/configuration", GetAsync)
            .WithName("NhAssistantAdminGetAgentConfiguration")
            .WithSummary("Get the run configuration of an agent")
            .WithDescription("Returns the agent's skills and triggers in code and as data, with the data that no longer fits the code and why. Only agents whose runs are configured in code have one.")
            .Produces<NhAssistantAgentConfigurationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("agents/{id}/skills/{skillId}", SaveSkillAsync)
            .WithName("NhAssistantAdminSaveDataSkill")
            .WithSummary("Add or change a skill defined as data")
            .WithDescription("A new skill starts in shadow; put it live with POST agents/{id}/skills/{skillId}/shadow. A change stores the next version, which takes effect at once in the skill's mode, when expectedVersion is the live version; otherwise 409. Tools stay within the agent's own selectors and every name must be registered in code (400). A skill in code cannot change (409).")
            .Produces<NhAssistantDataSkillDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("agents/{id}/skills/{skillId}", RemoveSkillAsync)
            .WithName("NhAssistantAdminRemoveDataSkill")
            .WithSummary("Remove a skill defined as data")
            .WithDescription("Takes the skill out of effect; its versions stay as history. A skill that a data trigger names (409) or that is declared in code (409) stays.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/skills/{skillId}/versions", GetSkillVersionsAsync)
            .WithName("NhAssistantAdminGetDataSkillVersions")
            .WithSummary("List the versions of a skill defined as data")
            .WithDescription("Returns every version, newest first, with its status, the hash of its instructions, who stored it when and why.")
            .Produces<NhAssistantDataSkillVersionDto[]>();
        admin.MapPut("agents/{id}/triggers/{triggerId}", SaveTriggerAsync)
            .WithName("NhAssistantAdminSaveDataTrigger")
            .WithSummary("Add or change a trigger defined as data")
            .WithDescription("Links a trigger event or a schedule (cron, time zone, subject set, message) to the agent, in one of its skills or routed by the agent. A new trigger starts in shadow; a change keeps its mode when expectedVersion is current, otherwise 409. Every name must be registered in code (400). A trigger in code cannot change (409).")
            .Produces<NhAssistantDataTriggerDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("agents/{id}/triggers/{triggerId}", RemoveTriggerAsync)
            .WithName("NhAssistantAdminRemoveDataTrigger")
            .WithSummary("Remove a trigger defined as data")
            .WithDescription("Removes the trigger; runs it started finish. A trigger in code cannot be removed (409).")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/triggers/{triggerId}/shadow", SetTriggerShadowAsync)
            .WithName("NhAssistantAdminSetDataTriggerShadow")
            .WithSummary("Put a trigger defined as data in shadow or live")
            .WithDescription("mode shadow: the trigger's runs work in the agent's shadow threads, which only administrators see, where nobody is notified and nothing executes. live: the runs work in the mode of the agent and the skill.")
            .Produces<NhAssistantDataTriggerDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
    }

    private static IResult GetCatalog(INhAssistantAgentConfiguration configuration)
    {
        return NhAssistantAdminEndpoints.Json(
            configuration.GetCatalog(),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantRunCatalogDto);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext httpContext, INhAssistantAgentConfiguration configuration)
    {
        var result = await configuration.GetAsync(id, httpContext.RequestAborted);
        return result is null
            ? NhAssistantAdminEndpoints.Failure(Common.Models.TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."))
            : NhAssistantAdminEndpoints.Json(result, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentConfigurationDto);
    }

    private static async Task<IResult> GetSkillVersionsAsync(
        string id,
        string skillId,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var versions = await configuration.GetSkillVersionsAsync(id, skillId, httpContext.RequestAborted);
        return NhAssistantAdminEndpoints.Json(
            versions.ToArray(),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataSkillVersionDtoArray);
    }

    private static async Task<IResult> SaveSkillAsync(
        string id,
        string skillId,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataSkillRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await configuration.SaveSkillAsync(id, skillId, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataSkillDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> RemoveSkillAsync(
        string id,
        string skillId,
        int? expectedVersion,
        string? reason,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }

        var result = await configuration.RemoveSkillAsync(id, skillId, expectedVersion, actor, reason, httpContext.RequestAborted);
        return result.Success ? TypedResults.NoContent() : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> SaveTriggerAsync(
        string id,
        string triggerId,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataTriggerRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await configuration.SaveTriggerAsync(id, triggerId, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataTriggerDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> RemoveTriggerAsync(
        string id,
        string triggerId,
        int? expectedVersion,
        string? reason,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }

        var result = await configuration.RemoveTriggerAsync(id, triggerId, expectedVersion, actor, reason, httpContext.RequestAborted);
        return result.Success ? TypedResults.NoContent() : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> SetTriggerShadowAsync(
        string id,
        string triggerId,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantShadowRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.Mode is null)
        {
            return NhAssistantAdminEndpoints.MissingField("mode");
        }

        // A data trigger has no mode in code to return to.
        if (body.Mode is not (NhAssistantShadowChoices.Shadow or NhAssistantShadowChoices.Live))
        {
            return NhAssistantAdminEndpoints.Failure(NhAssistantValidationErrors.Failed("mode", NhAssistantFieldErrors.Invalid));
        }

        var result = await configuration.SetTriggerShadowAsync(
            id,
            triggerId,
            body.Mode == NhAssistantShadowChoices.Shadow,
            actor,
            body.Reason,
            httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataTriggerDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }
}
