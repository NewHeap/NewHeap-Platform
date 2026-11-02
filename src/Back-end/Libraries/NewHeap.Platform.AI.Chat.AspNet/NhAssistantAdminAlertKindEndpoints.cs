using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The administration of alert kinds (FB87 phase 2): every kind with the bounds its code sets, the
/// administrator's settings and the numbers of its alerts, and the change of one kind's settings within
/// those bounds.
/// </summary>
internal static class NhAssistantAdminAlertKindEndpoints
{
    private static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    /// <summary>
    /// Maps the endpoints into the administration group, which already requires the admin policy.
    /// </summary>
    public static void MapAdmin(RouteGroupBuilder admin)
    {
        admin.MapGet("alert-kinds", GetAsync)
            .WithName("NhAssistantAdminGetAlertKinds")
            .WithSummary("List the alert kinds with their settings and numbers")
            .WithDescription("Returns every alert kind the application registers, ordered by id: its name key, urgency and whether it is required; administratorMayChange, what its code lets an administrator change (switchOff, digestTimes, workdaysOnly, immediatelyFrom, quietHours); codeDelivery, the default delivery its code declares; settings, the administrator's settings with their version (0 when nobody changed the kind); enabled and delivery, what applies now (the code's merged with the settings the code allows; a person's own choices apply on top). numbers counts the kind's live alerts raised in the period: raised, and per delivery to one person delivered, read, dismissed, resolvedBeforeReading, withheld and of those muted; a delivery dropped because its person no longer qualified does not count. shadow lists per agent in shadow the alerts it raised in the period and wouldHaveSent. Closed alerts are purged after the alert retention, so the numbers cover what the store keeps. The period defaults to the last 30 days on the application's clock and spans at most 366 days; an invalid period answers 400 assistant-message-invalid.")
            .Produces<NhAssistantAdminAlertKindsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        admin.MapPut("alert-kinds/{id}", PutAsync)
            .WithName("NhAssistantAdminSetAlertKind")
            .WithSummary("Change the settings of an alert kind")
            .WithDescription("Takes the kind's complete settings: enabled (required) and delivery, null for the code's, with mode immediately or daily-digest, digestTimes (one to four distinct times of day as HH:mm, for daily-digest), workdaysOnly and immediatelyFrom (info, warning or urgent, for daily-digest) and quietHours with a start and an end as HH:mm; with expectedVersion, the settings' current version (0 before the first change), and an optional reason of at most 500 characters, kept with the settings and left out of the audit. Only what the kind's code allows may change: switching off a required kind, or one whose code does not allow it, answers 400 assistant-alert-kind-switch-off-not-allowed; another part of the delivery 400 assistant-alert-kind-delivery-not-allowed with the parts as field errors; a value that repeats the code's is accepted and a delivery equal to the code's is stored as the code's. A malformed delivery answers 400 assistant-alert-kind-delivery-invalid with the parts as field errors, a missing enabled or a reason that is too long 400 assistant-validation, an unknown kind 404 assistant-alert-kind-unknown and a stale version 409 assistant-version-conflict. Every change is the next version, audited as AdminAlertKindChanged with the names of the settings that changed, and applies to the next alert raised or delivered on every host; the same settings again change nothing. A kind that is off raises nothing (assistant-alert-kind-off), its alerts can still be resolved and its waiting deliveries are withheld as kind-off. Returns the kind without numbers.")
            .Produces<NhAssistantAdminAlertKindDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        NhAssistantAdminAlertKinds kinds,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var end = (to ?? kinds.Now).ToUniversalTime();
        var start = (from ?? end - DefaultPeriod).ToUniversalTime();
        if (start >= end || end - start > MaxPeriod)
        {
            return NhAssistantHttpErrors.Error(NhAssistantErrorCodes.MessageInvalid);
        }

        var view = await kinds.GetAsync(start, end, httpContext.RequestAborted);
        return NhAssistantAdminEndpoints.Json(view, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAdminAlertKindsDto);
    }

    private static async Task<IResult> PutAsync(
        string id,
        HttpContext httpContext,
        NhAssistantAdminAlertKinds kinds)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(
            httpContext,
            NhAssistantAlertJsonSerializerContext.Default.NhAssistantAdminAlertKindRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }

        var result = await kinds.SaveAsync(id, body, actor, httpContext.RequestAborted);
        return result.Success
            ? NhAssistantAdminEndpoints.Json(result.Data!, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAdminAlertKindDto)
            : NhAssistantAdminEndpoints.Failure(result);
    }
}
