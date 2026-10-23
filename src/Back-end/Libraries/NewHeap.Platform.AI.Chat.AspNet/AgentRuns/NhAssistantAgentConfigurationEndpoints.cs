using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
            .WithDescription("Returns the trigger events, subject sets and rule handlers registered in code, the approval policies and the chat profiles that skills and triggers defined as data can name, and the subject types with their names in the request's language, entity, link template and whether they can be searched.")
            .Produces<NhAssistantRunCatalogDto>();
        admin.MapGet("accounts", GetAccountsAsync)
            .WithName("NhAssistantAdminGetAccounts")
            .WithSummary("Find the accounts an agent can answer to")
            .WithDescription("With query (2 to 100 characters) searches the application's accounts by name or e-mail address through INhActorSearch, at most limit (default 20, at most 50); without a registered search 404 assistant-account-search-unavailable. With ids (comma-separated actor ids, at most 50 distinct ids of at most 256 characters each, otherwise 400 assistant-validation with ids invalid) returns the names the application knows for them, leaving out unknown ids; a client with more ids asks in batches of 50. Whether an account may answer for an agent is checked when the run settings are saved.")
            .Produces<NhAssistantAccountDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapGet("agents/{id}/configuration", GetAsync)
            .WithName("NhAssistantAdminGetAgentConfiguration")
            .WithSummary("Get the run configuration of an agent")
            .WithDescription("Returns the agent's skills and triggers in code and as data, the run settings of an agent created in the administration, and the data that no longer fits the code and why. Agents in code without ConfigureAgent have none (404).")
            .Produces<NhAssistantAgentConfigurationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("agents/{id}/run-settings", SaveRunSettingsAsync)
            .WithName("NhAssistantAdminSaveAgentRunSettings")
            .WithSummary("Add or change the run settings of an agent created in the administration")
            .WithDescription("Whom the agent answers to (agent, subject-responsible or service-account with serviceAccountActorId), its approval policy, budget, run ceiling, retries, gateway reads, triage profile and rule handler. Grants are never data: in headless runs the agent acts with the rights of the account it answers to, within its tool selectors, recorded as grant source accountable. The agent then gets skills and triggers as data. expectedVersion must be current, otherwise 409; an agent in code cannot change (409).")
            .Produces<NhAssistantAgentRunSettingsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("agents/{id}/run-settings", RemoveRunSettingsAsync)
            .WithName("NhAssistantAdminRemoveAgentRunSettings")
            .WithSummary("Remove the run settings of an agent created in the administration")
            .WithDescription("The agent then only chats. While data skills, triggers or consultations belong to it, 409.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPut("agents/{id}/skills/{skillId}", SaveSkillAsync)
            .WithName("NhAssistantAdminSaveDataSkill")
            .WithSummary("Add or change a skill defined as data")
            .WithDescription("A new skill starts in shadow; put it live with POST agents/{id}/skills/{skillId}/shadow. A change stores the next version when expectedVersion is the live version, otherwise 409: with apply live (default) it takes effect at once in the skill's mode; with apply shadow it becomes the candidate that runs in the agent's shadow threads next to the live version until it is promoted or discarded. Tools stay within the agent's own selectors and every name must be registered in code (400). A skill in code cannot change (409).")
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
        admin.MapPost("agents/{id}/skills/{skillId}/candidate/promote", PromoteCandidateAsync)
            .WithName("NhAssistantAdminPromoteSkillCandidate")
            .WithSummary("Promote the candidate of a skill defined as data")
            .WithDescription("Makes the candidate of version the live version; the live version stays as history and new runs use the candidate live. Without a candidate 404; for another version 409.")
            .Produces<NhAssistantDataSkillDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost("agents/{id}/skills/{skillId}/candidate/discard", DiscardCandidateAsync)
            .WithName("NhAssistantAdminDiscardSkillCandidate")
            .WithSummary("Discard the candidate of a skill defined as data")
            .WithDescription("The candidate of version stays as history; its queued runs end. Without a candidate 404; for another version 409.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/skills/{skillId}/candidate/comparison", CompareCandidateAsync)
            .WithName("NhAssistantAdminCompareSkillCandidate")
            .WithSummary("Compare the candidate of a skill with its live version")
            .WithDescription("Runs, outcomes, usage and proposals per tool of the live version and of the candidate since the candidate was saved (or from), until to, with what people decided about the live version's proposals and how many events both handled. Counts only.")
            .Produces<NhAssistantSkillComparisonDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
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
        admin.MapPut("agents/{id}/consultations/{subjectType}", SaveConsultationAsync)
            .WithName("NhAssistantAdminSaveDataConsultation")
            .WithSummary("Add or change a consultation defined as data")
            .WithDescription("Lets other assistants consult the agent about subjects of one type: description, instructions, who may ask (minimumRole and an askerPolicy listed with AddAgentPolicy), the routes of its proposals, the proposal role and approval policy, limits, the request tool, MCP exposure and the classification of its answers, within the ceiling in code (UseConsultationsAsData, and ConsultableAsData for an agent in code). It is in effect once saved and answers when an asking agent selects its generated tool. A route outside the ceiling is refused with routes not-allowed (400); an agent that may get none (409 assistant-consultations-not-allowed), an agent created in the administration without run settings (409 assistant-agent-not-configured), a type the agent allows consultation about in code (409 assistant-consultation-in-code), more than 20 per agent (409 assistant-configuration-limit) and a stale expectedVersion (409) are refused.")
            .Produces<NhAssistantDataConsultationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("agents/{id}/consultations/{subjectType}", RemoveConsultationAsync)
            .WithName("NhAssistantAdminRemoveDataConsultation")
            .WithDescription("Removes the consultation; its tools disappear and a later call of them fails with assistant-consult-agent-unavailable, while its recorded proposals can still be decided. Without such a consultation 404 assistant-consultation-not-found; a consultation in code cannot be removed (409).")
            .WithSummary("Remove a consultation defined as data")
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

    private static async Task<IResult> GetAccountsAsync(
        string? query,
        string? ids,
        int? limit,
        HttpContext httpContext,
        NhAssistantAccounts accounts)
    {
        var found = string.IsNullOrWhiteSpace(ids)
            ? await accounts.SearchAsync(query, limit, httpContext.RequestAborted)
            : await accounts.FindAsync(ids, httpContext.RequestAborted);
        return found.Success
            ? NhAssistantAdminEndpoints.Json(
                found.Data!.ToArray(),
                NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAccountDtoArray)
            : NhAssistantAdminEndpoints.Failure(found);
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

    private static async Task<IResult> SaveRunSettingsAsync(
        string id,
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
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentRunSettingsRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await configuration.SaveRunSettingsAsync(id, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentRunSettingsDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> RemoveRunSettingsAsync(
        string id,
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

        var result = await configuration.RemoveRunSettingsAsync(id, expectedVersion, actor, reason, httpContext.RequestAborted);
        return result.Success ? TypedResults.NoContent() : NhAssistantAdminEndpoints.Failure(result);
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

    private static async Task<IResult> PromoteCandidateAsync(
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
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSkillCandidateRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.Version is not { } version)
        {
            return NhAssistantAdminEndpoints.MissingField("version");
        }

        var result = await configuration.PromoteSkillCandidateAsync(id, skillId, version, actor, body.Reason, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataSkillDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> DiscardCandidateAsync(
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
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSkillCandidateRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.Version is not { } version)
        {
            return NhAssistantAdminEndpoints.MissingField("version");
        }

        var result = await configuration.DiscardSkillCandidateAsync(id, skillId, version, actor, body.Reason, httpContext.RequestAborted);
        return result.Success ? TypedResults.NoContent() : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> CompareCandidateAsync(
        string id,
        string skillId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var result = await configuration.CompareSkillCandidateAsync(id, skillId, from, to, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantSkillComparisonDto)
            : NhAssistantAdminEndpoints.Failure(result);
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

    private static async Task<IResult> SaveConsultationAsync(
        string id,
        string subjectType,
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
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataConsultationRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await configuration.SaveConsultationAsync(id, subjectType, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantDataConsultationDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> RemoveConsultationAsync(
        string id,
        string subjectType,
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

        var result = await configuration.RemoveConsultationAsync(id, subjectType, expectedVersion, actor, reason, httpContext.RequestAborted);
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
