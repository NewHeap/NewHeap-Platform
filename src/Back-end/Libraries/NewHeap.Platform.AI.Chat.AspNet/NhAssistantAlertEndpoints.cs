using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using static NewHeap.Platform.AI.Chat.AspNet.NhAssistantEndpointRouteBuilderExtensions;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The HTTP API of a person's alerts (FB87): their own list, the counts of a badge, and marking one
/// alert read or dismissing it. Every endpoint sits inside the kill-switch group with the assistant
/// access policy and reads only the caller's own deliveries; an impersonation session reads and changes
/// them as the person, as every assistant endpoint does, and its dismissals name the user who
/// impersonates.
/// </summary>
internal static class NhAssistantAlertEndpoints
{
    /// <summary>The most alerts one page lists, as the approval inbox pages.</summary>
    private const int MaxItemsPerPage = 100;

    public static void Map(RouteGroupBuilder enabled)
    {
        enabled.MapGet("alerts", ListAsync)
            .WithName("NhAssistantListAlerts")
            .WithSummary("List my alerts")
            .WithDescription("Returns the caller's own alerts, the most recently raised first, paged (page from 1, itemsPerPage 1 to 100, 20 by default), with each alert's title and text written in the caller's language, its kind and the kind's name key, its urgency (info, warning or urgent), its subject with the parent it belongs to and their links, and the caller's state: status is unread or read for an open alert, resolved or expired for a closed one, and dismissed once the caller dismissed it; a delivery that was withheld from the caller's channels, such as for a kind they muted, is listed as unread with isWithheld and its withheldReason. status filters on one of those, on open (unread and read) or on closed (resolved and expired); without it the list shows every alert the caller did not dismiss. kind (repeated, at most 20), subjectType with subjectId, and urgency (repeated) filter further. grouped=true groups the alerts by the parent their subject belongs to, or by their own subject without one: groups by their most recent alert, a group's own subject first, with per group on the page its subjects, how many alerts it has on the page and in total, and whether it continues on the page before or after. Every alert passes the recipient check with the caller's tenant: an alert about a subject the caller may no longer read, or of a kind the host no longer registers, is left out. The list reads the newest 500 alerts the filter selects. A malformed filter answers 400 assistant-alert-filter-invalid.")
            .Produces<NhAssistantAlertListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        enabled.MapGet("alerts/summary", SummarizeAsync)
            .WithName("NhAssistantGetAlertSummary")
            .WithSummary("Count my unread alerts")
            .WithDescription("Counts the caller's unread open alerts for a badge, in total and per urgency, with the visibility of GET alerts: an alert about a subject the caller may no longer read is not counted, nor one withheld because the caller muted its kind. kind (repeated, at most 20) and subjectType with subjectId narrow the counts, for example to one object's page. Counts only, no alert content. A malformed filter answers 400 assistant-alert-filter-invalid.")
            .Produces<NhAssistantAlertSummaryDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden);
        enabled.MapPost("alerts/{id:guid}/read", MarkReadAsync)
            .WithName("NhAssistantMarkAlertRead")
            .WithSummary("Mark an alert read")
            .WithDescription("Marks the caller's own alert read and returns it as the list now shows it; a delivery of it that still waits, such as for the morning digest, is then not sent. A withheld one is read as well. Reading it again, or reading an alert the caller dismissed, changes nothing. An alert of another person, one about a subject the caller may no longer read, and an unknown one answer 404 assistant-alert-not-found.")
            .Produces<NhAssistantAlertDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapPost("alerts/{id:guid}/dismiss", DismissAsync)
            .WithName("NhAssistantDismissAlert")
            .WithSummary("Dismiss an alert")
            .WithDescription("Dismisses the caller's own alert, also a resolved or expired one, and returns it as the list now shows it: it leaves the caller's list, and raising it again does not reach the caller until its key changes. The first dismissal is audited as AgentAlertDismissed, content-free and, in an impersonation session, with the user who impersonates; dismissing it again changes nothing. An alert of another person, one about a subject the caller may no longer read, and an unknown one answer 404 assistant-alert-not-found.")
            .Produces<NhAssistantAlertDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        NhAssistantAlertList alerts,
        string? status,
        string[]? kind,
        string? subjectType,
        string? subjectId,
        string[]? urgency,
        int? page,
        int? itemsPerPage,
        bool? grouped)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }

        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        var query = NhAssistantAlertQuery.Read(status, kind, subjectType, subjectId, urgency);
        if (pageNumber < 1 || size is < 1 or > MaxItemsPerPage || !query.Success)
        {
            return Error(NhAssistantAlertCodes.FilterInvalid);
        }

        var list = await alerts.ListAsync(caller.Data!, query.Data!, pageNumber, size, grouped ?? false, httpContext.RequestAborted);
        return TypedResults.Json(list, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertListDto);
    }

    private static async Task<IResult> SummarizeAsync(
        HttpContext httpContext,
        NhAssistantAlertList alerts,
        string[]? kind,
        string? subjectType,
        string? subjectId)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }

        var query = NhAssistantAlertQuery.Read(null, kind, subjectType, subjectId, null);
        if (!query.Success)
        {
            return Error(NhAssistantAlertCodes.FilterInvalid);
        }

        var summary = await alerts.SummarizeAsync(caller.Data!, query.Data!, httpContext.RequestAborted);
        return TypedResults.Json(summary, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertSummaryDto);
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantAlertList alerts)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }

        var read = await alerts.MarkReadAsync(caller.Data!, id, httpContext.RequestAborted);
        return read.Success
            ? TypedResults.Json(read.Data!, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertDto)
            : FromFailure(read);
    }

    private static async Task<IResult> DismissAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantAlertList alerts)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }

        var dismissed = await alerts.DismissAsync(
            caller.Data!,
            id,
            NhAssistantImpersonation.OriginOf(httpContext.User),
            httpContext.RequestAborted);
        return dismissed.Success
            ? TypedResults.Json(dismissed.Data!, NhAssistantAlertJsonSerializerContext.Default.NhAssistantAlertDto)
            : FromFailure(dismissed);
    }
}
