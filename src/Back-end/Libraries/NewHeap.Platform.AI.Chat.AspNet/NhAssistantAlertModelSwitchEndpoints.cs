using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The administration of the model tool of alerts (FB87-9): per agent the kinds it lists that allow a
/// model, with their switches, and the change of one switch with its history.
/// </summary>
internal static class NhAssistantAlertModelSwitchEndpoints
{
    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("agents/{id}/alert-model", GetAsync)
            .WithName("NhAssistantAdminGetAgentAlertModel")
            .WithSummary("List the alert kinds an agent's model may raise")
            .WithDescription("Returns the alert kinds the agent lists with Alerts(...) whose code lets a model raise them (RaisedByModel), ordered by id, each with its name key, when it applies and the parameters the model fills (name, type choice, whole-number, date or text, description, choices, bounds, maxLength and whether it forms the key), whether the kind is on (kindEnabled), and the switch in effect: off (the model is not offered the kind), shadow (the model's alerts are recorded as shadow alerts that reach nobody and count as wouldHaveSent in GET admin/alert-kinds) or live (they reach the people the kind chooses, checked as every alert's recipients are). version is the switch's current version, which a change expects; contractChanged says that the kind's template changed since the switch was made, so it reads as off until it is switched again, first in shadow. applies is false for an agent that does no work of its own, such as a chat-only agent or a shadow variant. 404 for an unknown agent.")
            .Produces<NhAssistantAlertModelListDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        admin.MapPut("agents/{id}/alert-kinds/{kindId}/model", SetAsync)
            .WithName("NhAssistantAdminSetAgentAlertModel")
            .WithSummary("Switch whether an agent's model raises alerts of a kind")
            .WithDescription("mode off, shadow or live with expectedVersion, the switch's current version (0 before the first switch), and an optional reason of at most 500 characters, which is kept with the version and left out of the audit. Live only from shadow (409 assistant-alert-model-shadow-first); a kind whose code does not let a model raise it, that the agent does not list, or an agent without work of its own answers 409 assistant-alert-model-not-allowed; an unknown kind 404 assistant-alert-kind-unknown; a stale version 409 assistant-version-conflict. Every change is the next version, audited as AdminAlertModelSwitchChanged, and applies to the next run on every host; the same mode again changes nothing. The model's tool assistant.alert.raise is offered only in a run of the agent on one subject, never in a person's chat, a consultation or a run on the whole application, and the model chooses neither the subject nor the recipients.")
            .Produces<NhAssistantAlertModelSwitchDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        admin.MapGet("agents/{id}/alert-kinds/{kindId}/model/versions", GetVersionsAsync)
            .WithName("NhAssistantAdminGetAgentAlertModelVersions")
            .WithSummary("List the versions of a switch of the model tool of alerts")
            .WithDescription("Returns every version of the agent's switch for the alert kind, newest first, with its mode, who switched it when, the reason and the template hash it was made for; empty when nobody switched it.")
            .Produces<NhAssistantAlertModelVersionDto[]>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> GetAsync(string id, HttpContext httpContext, INhAssistantAlertModelSwitches switches)
    {
        var result = await switches.GetAsync(id, httpContext.RequestAborted);
        return result is null
            ? NhAssistantAdminEndpoints.Failure(Common.Models.TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."))
            : NhAssistantAdminEndpoints.Json(result, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertModelListDto);
    }

    private static async Task<IResult> SetAsync(
        string id,
        string kindId,
        HttpContext httpContext,
        INhAssistantAlertModelSwitches switches)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertModelRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await switches.SetAsync(id, kindId, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertModelSwitchDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }

    private static async Task<IResult> GetVersionsAsync(
        string id,
        string kindId,
        HttpContext httpContext,
        INhAssistantAlertModelSwitches switches)
    {
        var versions = await switches.GetVersionsAsync(id, kindId, httpContext.RequestAborted);
        return NhAssistantAdminEndpoints.Json(
            versions.ToArray(),
            NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertModelVersionDtoArray);
    }
}
