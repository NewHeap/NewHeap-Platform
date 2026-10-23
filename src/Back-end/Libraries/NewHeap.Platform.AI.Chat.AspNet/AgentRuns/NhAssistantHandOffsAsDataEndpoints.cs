using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Hand-offs defined as data and the shadow variants of an agent for administrators.
/// </summary>
internal static class NhAssistantHandOffsAsDataEndpoints
{
    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/hand-offs", GetAsync)
            .WithName("NhAssistantAdminGetAgentHandOffs")
            .WithSummary("Get the hand-offs an agent accepts")
            .WithDescription("Returns the hand-offs the agent accepts in code (AcceptHandOffs, read-only) and as data with whether each is in effect and why not, whether the agent has the run configuration a hand-off needs, and what the ceiling in code (UseHandOffsAsData) lets administrators add: per subject type the agents that may hand off to it, and the highest daily limit. The ceiling is null when the agent may accept none. An unknown agent answers 404.")
            .Produces<NhAssistantAgentHandOffsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("agents/{id}/hand-offs/{subjectType}", SaveAsync)
            .WithName("NhAssistantAdminSaveDataHandOff")
            .WithSummary("Add or change a hand-off defined as data")
            .WithDescription("Lets the agents in sources hand work on subjects of the type to the agent, with a description for their models, a follow-up to the source (notifySource) and a daily limit per source and subject, within the ceiling in code. It is in effect once saved: a source's run gets the generated tool assistant.handoff.{id}.{subjectType} when its tool selectors select it, and depth, scope, daily limit and shadow rules hold as for hand-offs in code. Checked as startup checks AcceptHandOffs: a source outside the ceiling answers 400 with sources not-allowed, one without a run configuration sources not-found, a type the ceiling does not connect the agent about subjectType not-allowed, one it does not work on subjectType invalid, a limit above the ceiling's maxPerSubjectPerDay not-allowed; an agent the ceiling connects to no agent 409 assistant-handoffs-not-allowed, a type it accepts in code 409 assistant-handoff-in-code, an agent without run configuration 409 assistant-agent-not-configured, more than 20 per agent 409 assistant-configuration-limit and a stale expectedVersion 409 assistant-version-conflict.")
            .Produces<NhAssistantDataHandOffDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("agents/{id}/hand-offs/{subjectType}", RemoveAsync)
            .WithName("NhAssistantAdminRemoveDataHandOff")
            .WithSummary("Remove a hand-off defined as data")
            .WithDescription("Removes the hand-off: its tool disappears from the sources' next turns and a later call is refused with assistant-handoff-not-accepted, while runs it started finish. Without such a hand-off 404 assistant-handoff-not-found; a hand-off in code cannot be removed (409 assistant-handoff-in-code); a stale expectedVersion answers 409.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/shadow-variants", GetShadowVariantsAsync)
            .WithName("NhAssistantAdminGetAgentShadowVariants")
            .WithSummary("List the shadow variants of an agent")
            .WithDescription("Returns the agent's shadow variants in code (ShadowOf) and created as data, each with whether it has run settings, whether it receives the agent's triggers now and why a data variant is out of effect, and whether the ceiling in code (UseShadowVariantsAsData) lets administrators create more and how many. A data variant is created with POST agents and shadowOf, edited like every agent created in the administration and removed with DELETE agents/{id}. An unknown agent answers 404.")
            .Produces<NhAssistantShadowVariantsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext httpContext, INhAssistantDataHandOffs handOffs)
    {
        var result = await handOffs.GetAsync(id, httpContext.RequestAborted);
        return result is null
            ? NhAssistantAdminEndpoints.Failure(TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."))
            : NhAssistantAdminEndpoints.Json(result, NhAssistantHandOffsAsDataJsonSerializerContext.Default.NhAssistantAgentHandOffsDto);
    }

    private static async Task<IResult> SaveAsync(
        string id,
        string subjectType,
        HttpContext httpContext,
        INhAssistantDataHandOffs handOffs)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantHandOffsAsDataJsonSerializerContext.Default.NhAssistantDataHandOffRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await handOffs.SaveAsync(id, subjectType, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantHandOffsAsDataJsonSerializerContext.Default.NhAssistantDataHandOffDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> RemoveAsync(
        string id,
        string subjectType,
        int? expectedVersion,
        string? reason,
        HttpContext httpContext,
        INhAssistantDataHandOffs handOffs)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }

        var result = await handOffs.RemoveAsync(id, subjectType, expectedVersion, actor, reason, httpContext.RequestAborted);
        return result.Success ? TypedResults.NoContent() : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> GetShadowVariantsAsync(string id, HttpContext httpContext, NhAssistantShadowVariants variants)
    {
        var result = await variants.ListAsync(id, httpContext.RequestAborted);
        return result is null
            ? NhAssistantAdminEndpoints.Failure(TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."))
            : NhAssistantAdminEndpoints.Json(result, NhAssistantHandOffsAsDataJsonSerializerContext.Default.NhAssistantShadowVariantsDto);
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantAgentHandOffsDto))]
[JsonSerializable(typeof(NhAssistantDataHandOffDto))]
[JsonSerializable(typeof(NhAssistantDataHandOffRequest))]
[JsonSerializable(typeof(NhAssistantShadowVariantsDto))]
internal sealed partial class NhAssistantHandOffsAsDataJsonSerializerContext : JsonSerializerContext;
