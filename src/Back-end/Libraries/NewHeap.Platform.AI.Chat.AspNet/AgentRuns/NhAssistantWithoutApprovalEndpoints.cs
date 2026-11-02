using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The administration of running a tool without approval (FB82): per agent the tools that allow it in
/// code, with their switches, and the change of one switch with its history.
/// </summary>
internal static class NhAssistantWithoutApprovalEndpoints
{
    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/without-approval", GetAsync)
            .WithName("NhAssistantAdminGetAgentWithoutApproval")
            .WithSummary("List the tools an agent may run without approval")
            .WithDescription("Returns the agent's tools whose code allows running without approval (AllowWithoutApproval) with the switch in effect: off, shadow (every call still becomes a proposal and the platform records that it would have run without approval) or live (the agent's own work runs it without a proposal, audited as run without approval). version is the switch's current version, which a change expects; contractChanged says that the tool's contract changed since the switch was made, so it reads as off until it is switched again, first in shadow. applies is false for an agent that does no work of its own, such as a chat-only agent or a shadow variant. 404 for an unknown agent.")
            .Produces<NhAssistantWithoutApprovalListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("agents/{id}/tools/{toolId}/without-approval", SetAsync)
            .WithName("NhAssistantAdminSetAgentWithoutApproval")
            .WithSummary("Switch whether an agent runs a tool without approval")
            .WithDescription("mode off, shadow or live with expectedVersion, the switch's current version (0 before the first switch), and an optional reason of at most 500 characters, which is kept with the version and left out of the audit. Live only from shadow (409 assistant-without-approval-shadow-first); a tool that does not allow it in code, is not among the agent's tools, or an agent without work of its own answers 409 assistant-without-approval-not-allowed; a stale version 409 assistant-version-conflict. Every change is the next version, audited as AdminAgentToolWithoutApprovalChanged, and applies to the next call on every host; the same mode again changes nothing. Only an agent's own work runs without approval: never a person's chat, a consultation, a rule turn, a shadow run or a call for another object.")
            .Produces<NhAssistantWithoutApprovalSwitchDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/tools/{toolId}/without-approval/versions", GetVersionsAsync)
            .WithName("NhAssistantAdminGetAgentWithoutApprovalVersions")
            .WithSummary("List the versions of a switch for running a tool without approval")
            .WithDescription("Returns every version of the agent's switch for the tool, newest first, with its mode, who switched it when, the reason and the contract hash it was made for; empty when nobody switched it.")
            .Produces<NhAssistantWithoutApprovalVersionDto[]>();
    }

    private static async Task<IResult> GetAsync(string id, HttpContext httpContext, INhAssistantWithoutApprovalSwitches switches)
    {
        var result = await switches.GetAsync(id, httpContext.RequestAborted);
        return result is null
            ? NhAssistantAdminEndpoints.Failure(Common.Models.TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."))
            : NhAssistantAdminEndpoints.Json(result, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantWithoutApprovalListDto);
    }

    private static async Task<IResult> SetAsync(
        string id,
        string toolId,
        HttpContext httpContext,
        INhAssistantWithoutApprovalSwitches switches)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantWithoutApprovalRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await switches.SetAsync(id, toolId, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantWithoutApprovalSwitchDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> GetVersionsAsync(
        string id,
        string toolId,
        HttpContext httpContext,
        INhAssistantWithoutApprovalSwitches switches)
    {
        var versions = await switches.GetVersionsAsync(id, toolId, httpContext.RequestAborted);
        return NhAssistantAdminEndpoints.Json(
            versions.ToArray(),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantWithoutApprovalVersionDtoArray);
    }
}
