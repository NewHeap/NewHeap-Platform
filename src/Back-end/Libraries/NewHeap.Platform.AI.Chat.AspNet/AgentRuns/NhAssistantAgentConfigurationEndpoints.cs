using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        admin.MapGet("instruction-assets", ListInstructionAssetsAsync)
            .WithName("NhAssistantAdminListInstructionAssets")
            .WithSummary("List the application's text assets for instructions")
            .WithDescription("Returns whether the application registered a text asset source (UseTextAssetSource) and the trusted system instructions it lists for administrators to choose, with id, version and the hash its manifest states, never their content. An application that lists none answers an empty list; an administrator then names the id and version. A source that fails answers 503 assistant-instructions-asset-unavailable.")
            .Produces<NhAssistantInstructionAssetListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
        admin.MapGet("instruction-assets/{id}/versions/{version:int}", ReadInstructionAssetAsync)
            .WithName("NhAssistantAdminReadInstructionAsset")
            .WithSummary("Read the text of one of the application's text assets")
            .WithDescription("Returns the asset's text read-only with the SHA-256 of its content, which a data skill version or an agent created in the administration stores as instructionsAsset.hash, and problem asset-invalid for an asset that cannot be instructions. Without a source or without such an asset 404 assistant-instructions-asset-not-found; a source that fails 503 assistant-instructions-asset-unavailable.")
            .Produces<NhAssistantInstructionAssetTextDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status503ServiceUnavailable);
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
        admin.MapGet("agents/{id}/metadata", GetMetadataAsync)
            .WithName("NhAssistantAdminGetAgentMetadata")
            .WithSummary("Get the metadata of an agent")
            .WithDescription("Returns every entry of agent metadata the application registers with AddAgentMetadata, ordered by name, with the agent's value in effect and where it comes from (code, override or data), the value in code next to an override, whether the entry changes here (always for an agent created at runtime, for an agent in code only when the entry is editable), its size limit and the version a change sends as expectedVersion. NewHeap never interprets metadata. An unknown agent answers 404.")
            .Produces<NhAssistantAgentMetadataDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("agents/{id}/metadata/{name}", SaveMetadataAsync)
            .WithName("NhAssistantAdminSaveAgentMetadata")
            .WithSummary("Save one entry of an agent's metadata")
            .WithDescription("Stores value after the checks startup applies to values in code: it fits the entry's type exactly (value invalid), its size limit (value too-long) and every validator (field errors value or value.<field> with the validator's codes). For an agent created at runtime the change makes the agent's next version and expectedVersion, the agent's version, is required (400); for an agent in code only an editable entry changes, as an override with a version of its own (expectedVersion null for a new override), otherwise 409 assistant-metadata-read-only. A stale expectedVersion answers 409 assistant-version-conflict, an entry that is not registered 404 assistant-metadata-entry-not-found and an unknown agent 404. The audit names the entry, never its value.")
            .Produces<NhAssistantAgentMetadataDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapDelete("agents/{id}/metadata/{name}", RemoveMetadataAsync)
            .WithName("NhAssistantAdminRemoveAgentMetadata")
            .WithSummary("Remove one entry of an agent's metadata")
            .WithDescription("Removes the stored value: of an agent created at runtime as its next version, of an agent in code its override, so the value in code applies again; returns the entry as it is now. Without a stored value nothing changes; otherwise expectedVersion is required (400) and must be current (409). A read-only entry of an agent in code answers 409, an unknown entry or agent 404.")
            .Produces<NhAssistantAgentMetadataDto>()
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

    private static async Task<IResult> ListInstructionAssetsAsync(
        HttpContext httpContext,
        NhAssistantInstructionAssets instructionAssets,
        ILogger<NhAssistantInstructionAssets> logger)
    {
        IReadOnlyList<NhAiAssetManifest>? manifests;
        try
        {
            manifests = await instructionAssets.ListAsync(httpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("The application's text asset source failed to list its assets ({ExceptionType}).", exception.GetType().Name);
            return NhAssistantHttpErrors.Error(NhAssistantAdminErrorCodes.InstructionsAssetUnavailable);
        }

        // Only what may be instructions is offered: trusted system instructions of the application.
        var assets = (manifests ?? [])
            .Where(manifest => manifest is { Role: NhAiAssetRole.SystemInstructions, Trust: NhAiContextTrust.TrustedApplication })
            .Select(manifest => new NhAssistantInstructionAssetDto(manifest.Id, manifest.Version, manifest.ContentHash)
            {
                ReplacedByAssetId = manifest.ReplacedByAssetId,
                DeprecatedAt = manifest.DeprecatedAt
            })
            .ToArray();
        return NhAssistantAdminEndpoints.Json(
            new NhAssistantInstructionAssetListDto(manifests is not null, assets),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantInstructionAssetListDto);
    }

    private static async Task<IResult> ReadInstructionAssetAsync(
        string id,
        int version,
        HttpContext httpContext,
        NhAssistantInstructionAssets instructionAssets,
        ILogger<NhAssistantInstructionAssets> logger)
    {
        NhAssistantResolvedInstructions? read;
        try
        {
            read = await instructionAssets.ReadAsync(id, version, httpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("The application's text asset source failed to read an asset ({ExceptionType}).", exception.GetType().Name);
            return NhAssistantHttpErrors.Error(NhAssistantAdminErrorCodes.InstructionsAssetUnavailable);
        }

        if (read?.Asset is not { } asset)
        {
            return NhAssistantHttpErrors.Error(NhAssistantAdminErrorCodes.InstructionsAssetNotFound);
        }

        return NhAssistantAdminEndpoints.Json(
            new NhAssistantInstructionAssetTextDto(id, version, NhAssistantInstructionAssets.HashOf(asset.Content), asset.Content)
            {
                Problem = read.Problem
            },
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantInstructionAssetTextDto);
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

    private static async Task<IResult> GetMetadataAsync(string id, HttpContext httpContext, INhAssistantAgentConfiguration configuration)
    {
        var result = await configuration.GetMetadataAsync(id, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(
                result.Data!.ToArray(),
                NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentMetadataDtoArray)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> SaveMetadataAsync(
        string id,
        string name,
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
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentMetadataRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await configuration.SaveMetadataAsync(id, name, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentMetadataDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> RemoveMetadataAsync(
        string id,
        string name,
        int? expectedVersion,
        HttpContext httpContext,
        INhAssistantAgentConfiguration configuration)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }

        var result = await configuration.RemoveMetadataAsync(id, name, expectedVersion, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantAgentMetadataDto)
            : NhAssistantAdminEndpoints.Failure(result);
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
