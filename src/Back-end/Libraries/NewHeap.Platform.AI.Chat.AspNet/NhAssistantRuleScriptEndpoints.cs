using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// A script to check without saving it.
/// </summary>
/// <param name="Script">The body of an async JavaScript function.</param>
public sealed record NhAssistantRuleScriptCheckRequest(string? Script);

/// <summary>
/// The administration of rule scripts (RB-1 to RB-6) for an agent's own rules, under
/// <c>admin/agents/{id}/rule-script</c>, and for one of its skills, under
/// <c>admin/agents/{id}/skills/{skillId}/rule-script</c>. Every endpoint calls
/// <see cref="INhAssistantRuleScripts"/>, the service the agent builder's tools call too.
/// </summary>
internal static class NhAssistantRuleScriptEndpoints
{
    private const string AgentPrefix = "agents/{id}/rule-script";
    private const string SkillPrefix = "agents/{id}/skills/{skillId}/rule-script";

    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        Map(admin, AgentPrefix, "NhAssistantAdmin", "the agent's own rules");
        Map(admin, SkillPrefix, "NhAssistantAdminSkill", "the rules of one skill of the agent");
    }

    private static void Map(RouteGroupBuilder admin, string prefix, string name, string whose)
    {
        admin.MapGet(prefix, GetAsync)
            .WithName(name + "GetRuleScript")
            .WithSummary("Read a rule script with its versions")
            .WithDescription($"The rule script of {whose}: whether the code's ceiling (UseRuleScriptsAsData) allows it (allowed), its revision, which every change expects as expectedRevision (0 before the first version), the live version, the candidate and whether it runs in shadow, the code rule a live version takes the place of (codeRule), the ceiling's writes, writes per run, destructive tools and four eyes, and every version newest first with its status (live, candidate, superseded, discarded), script, hash, writes, author, the agent that wrote it for the author, reason and times. 404 for an unknown agent or skill.")
            .Produces<NhAssistantRuleScriptDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapGet(prefix + "/catalog", GetCatalogAsync)
            .WithName(name + "GetRuleScriptCatalog")
            .WithSummary("Read what a rule script sees")
            .WithDescription($"For {whose}: the tools its rules may use with their TypeScript-style signatures, whether each is a write, whether the ceiling lets a version opt in to it (inCeiling), whether a version with the writes of the comma-separated query writes (by default every write of the ceiling) may call it, and why not; the alert kinds the agent lists; the declarations of subject, inputs, run, alerts.raise, alerts.resolve, propose, done, handOver and useSkill; and the limits. Whether a run may call a tool still follows its grants. 404 for an unknown agent or skill, 409 assistant-rule-scripts-not-allowed outside the ceiling.")
            .Produces<NhAssistantRuleScriptCatalogDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost(prefix + "/check", CheckAsync)
            .WithName(name + "CheckRuleScript")
            .WithSummary("Check a script without saving it")
            .WithDescription("Whether script parses and fits the length; when not, the script code (ai-script-syntax-error or ai-script-too-long) with the line and column. Nothing runs and nothing is saved.")
            .Produces<NhAssistantRuleScriptCheckDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost(prefix + "/versions", SaveAsync)
            .WithName(name + "SaveRuleScriptVersion")
            .WithSummary("Save the next version of a rule script")
            .WithDescription("script (the body of an async JavaScript function, at most 10,000 characters), writes (the writing tools it may call, by id, within the ceiling), expectedRevision, an optional reason of at most 500 characters kept with the version and left out of the audit, live to save it straight live and shadow to let the candidate run in shadow at once. A newer version discards the candidate that waited; a live one supersedes the live version. A script that does not parse answers 400 with the field error script; a write outside the ceiling 400 assistant-rule-script-write-not-allowed; live under four eyes 409 assistant-rule-script-four-eyes; a stale revision 409 assistant-version-conflict; outside the ceiling 409 assistant-rule-scripts-not-allowed. Audited as AdminRuleScriptSaved, and as AdminRuleScriptLive or AdminRuleScriptShadowChanged.")
            .Produces<NhAssistantRuleScriptVersionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost(prefix + "/versions/{version:int}/dry-run", DryRunAsync)
            .WithName(name + "DryRunRuleScriptVersion")
            .WithSummary("Run a version on chosen subjects without effects")
            .WithDescription("subjects: one to twenty subjects by type and id (nh-system/global for the whole application). Each runs with the rights a headless run of the agent has on it; writes, proposals and alerts are recorded with what would become of them (would-run, would-propose, would-decide) and never happen, and nothing is delivered. Returns per subject its status, end, calls, writes, proposals, alerts raised and resolved and log; it is kept as the version's latest dry run, which going live shows. 400 assistant-rule-script-subjects-invalid, 404 for an unknown version, agent or skill. Audited as AdminRuleScriptDryRun without the subjects.")
            .Produces<NhAssistantRuleScriptRunRecordDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet(prefix + "/versions/{version:int}/dry-run", GetDryRunAsync)
            .WithName(name + "GetRuleScriptDryRun")
            .WithSummary("Read the latest dry run of a version")
            .WithDescription("The latest dry run of the version; 204 when it has none.")
            .Produces<NhAssistantRuleScriptRunRecordDto>()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        admin.MapGet(prefix + "/versions/{version:int}/shadow-runs", GetShadowRunsAsync)
            .WithName(name + "GetRuleScriptShadowRuns")
            .WithSummary("Read what a candidate did in shadow")
            .WithDescription("The newest shadow runs of the version, newest first, each with the run it went along with and what it would have done on the run's subject.")
            .Produces<NhAssistantRuleScriptRunRecordDto[]>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        admin.MapPost(prefix + "/versions/{version:int}/live", SetLiveAsync)
            .WithName(name + "SetRuleScriptVersionLive")
            .WithSummary("Set a candidate live")
            .WithDescription("expectedRevision and an optional reason. The live version is superseded and the next rule turn runs this one instead of the code rule. Returns the version with its latest dry run (latestDryRun, null when it has none). 409 assistant-rule-script-not-candidate, assistant-rule-script-four-eyes for the version's own author under four eyes, assistant-version-conflict; 404 assistant-rule-script-version-not-found. Audited as AdminRuleScriptLive.")
            .Produces<NhAssistantRuleScriptLiveDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost(prefix + "/versions/{version:int}/shadow", SetShadowAsync)
            .WithName(name + "SetRuleScriptVersionShadow")
            .WithSummary("Try a candidate in shadow, or stop")
            .WithDescription("shadow true lets the candidate run next to the live rules of every rule turn, on the same subject and inputs, without effects, recording what it would do as shadow runs; false stops it. expectedRevision and an optional reason. 409 assistant-rule-script-not-candidate or assistant-version-conflict. Audited as AdminRuleScriptShadowChanged.")
            .Produces<NhAssistantRuleScriptVersionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapPost(prefix + "/versions/{version:int}/discard", DiscardAsync)
            .WithName(name + "DiscardRuleScriptVersion")
            .WithSummary("Discard a candidate")
            .WithDescription("expectedRevision and an optional reason. The candidate stays as history; a live version is replaced by another, never discarded (409 assistant-rule-script-not-candidate). Audited as AdminRuleScriptDiscarded.")
            .Produces<NhAssistantRuleScriptVersionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet(prefix + "/compare", CompareAsync)
            .WithName(name + "CompareRuleScriptVersions")
            .WithSummary("Compare two versions of a rule script")
            .WithDescription("version, and with the other version: by default the live version, or for the live version the one before. Returns the script line by line (same, added, removed), the writes added and removed and the latest dry run of both. 404 assistant-rule-script-version-not-found.")
            .Produces<NhAssistantRuleScriptComparisonDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var result = await ruleScripts.GetAsync(id, SkillOf(httpContext), httpContext.RequestAborted);
        return result is null
            ? NhAssistantAdminEndpoints.Failure(TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent or skill was not found."))
            : NhAssistantAdminEndpoints.Json(result, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptDto);
    }

    private static async Task<IResult> GetCatalogAsync(string id, string? writes, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var asked = writes is null
            ? null
            : writes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await ruleScripts.GetCatalogAsync(id, SkillOf(httpContext), asked, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptCatalogDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> CheckAsync(string id, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptCheckRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.Script is null)
        {
            return NhAssistantAdminEndpoints.MissingField("script");
        }

        var result = await ruleScripts.CheckAsync(id, SkillOf(httpContext), body.Script, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptCheckDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> SaveAsync(string id, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptSaveRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await ruleScripts.SaveAsync(id, SkillOf(httpContext), body, new NhAssistantRuleScriptAuthor(actor), httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptVersionDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> DryRunAsync(string id, int version, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptDryRunRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await ruleScripts.DryRunAsync(id, SkillOf(httpContext), version, body, new NhAssistantRuleScriptAuthor(actor), httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptRunRecordDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> GetDryRunAsync(string id, int version, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var result = await ruleScripts.GetDryRunAsync(id, SkillOf(httpContext), version, httpContext.RequestAborted);
        return result is null
            ? TypedResults.NoContent()
            : NhAssistantAdminEndpoints.Json(result, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptRunRecordDto);
    }

    private static async Task<IResult> GetShadowRunsAsync(string id, int version, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var result = await ruleScripts.GetShadowRunsAsync(id, SkillOf(httpContext), version, httpContext.RequestAborted);
        return NhAssistantAdminEndpoints.Json(result.ToArray(), NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptRunRecordDtoArray);
    }

    private static async Task<IResult> SetLiveAsync(string id, int version, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptChangeRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await ruleScripts.SetLiveAsync(id, SkillOf(httpContext), version, body, new NhAssistantRuleScriptAuthor(actor), httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptLiveDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> SetShadowAsync(string id, int version, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptShadowRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await ruleScripts.SetShadowAsync(id, SkillOf(httpContext), version, body, new NhAssistantRuleScriptAuthor(actor), httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptVersionDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> DiscardAsync(string id, int version, HttpContext httpContext, INhAssistantRuleScripts ruleScripts)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptChangeRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await ruleScripts.DiscardAsync(id, SkillOf(httpContext), version, body, new NhAssistantRuleScriptAuthor(actor), httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptVersionDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> CompareAsync(
        string id,
        int? version,
        int? with,
        HttpContext httpContext,
        INhAssistantRuleScripts ruleScripts)
    {
        if (version is not { } compared)
        {
            return NhAssistantAdminEndpoints.MissingField("version");
        }

        var result = await ruleScripts.CompareAsync(id, SkillOf(httpContext), compared, with, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantRuleScriptJsonSerializerContext.Default.NhAssistantRuleScriptComparisonDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    /// <summary>
    /// The skill of a skill's route; <see langword="null"/> for the agent's own rules.
    /// </summary>
    private static string? SkillOf(HttpContext httpContext)
    {
        return httpContext.Request.RouteValues.TryGetValue("skillId", out var value) ? value as string : null;
    }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantRuleScriptDto))]
[JsonSerializable(typeof(NhAssistantRuleScriptCatalogDto))]
[JsonSerializable(typeof(NhAssistantRuleScriptCheckDto))]
[JsonSerializable(typeof(NhAssistantRuleScriptCheckRequest))]
[JsonSerializable(typeof(NhAssistantRuleScriptSaveRequest))]
[JsonSerializable(typeof(NhAssistantRuleScriptVersionDto))]
[JsonSerializable(typeof(NhAssistantRuleScriptDryRunRequest))]
[JsonSerializable(typeof(NhAssistantRuleScriptRunRecordDto))]
[JsonSerializable(typeof(NhAssistantRuleScriptRunRecordDto[]))]
[JsonSerializable(typeof(NhAssistantRuleScriptChangeRequest))]
[JsonSerializable(typeof(NhAssistantRuleScriptShadowRequest))]
[JsonSerializable(typeof(NhAssistantRuleScriptLiveDto))]
[JsonSerializable(typeof(NhAssistantRuleScriptComparisonDto))]
internal sealed partial class NhAssistantRuleScriptJsonSerializerContext : JsonSerializerContext;
