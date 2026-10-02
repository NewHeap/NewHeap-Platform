using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using static NewHeap.Platform.AI.Chat.AspNet.NhAssistantEndpointRouteBuilderExtensions;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Read state, sharing and notification settings. All endpoints sit inside the kill-switch group.
/// </summary>
internal static class NhAssistantCollaborationEndpoints
{
    private const int MaxDirectoryResults = 20;

    public static void Map(RouteGroupBuilder enabled)
    {
        enabled.MapPost("conversations/{id:guid}/read", MarkReadAsync)
            .WithName("NhAssistantMarkRead")
            .WithSummary("Mark a conversation as read")
            .WithDescription("Raises the caller's read position to the given sequence, or to the latest message. It never lowers it.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapPost("conversations/{id:guid}/share-link", CreateShareLinkAsync)
            .WithName("NhAssistantCreateShareLink")
            .WithSummary("Create an invitation link")
            .WithDescription("Owner only. Returns a new invitation token; an earlier link stops working while people who joined stay.")
            .Produces<NhAssistantShareLinkDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapDelete("conversations/{id:guid}/share-link", RevokeShareLinkAsync)
            .WithName("NhAssistantRevokeShareLink")
            .WithSummary("Revoke the invitation link")
            .WithDescription("Owner only. The link stops working; people who joined stay until the owner removes them.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapPost("conversations/{id:guid}/join", JoinAsync)
            .WithName("NhAssistantJoinConversation")
            .WithSummary("Join a shared conversation")
            .WithDescription("Joins with the token of the current invitation link. The caller must belong to the owner's tenant and may use the agent.")
            .Produces<NhAssistantConversationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapGet("conversations/{id:guid}/participant-candidates", SearchCandidatesAsync)
            .WithName("NhAssistantSearchParticipantCandidates")
            .WithSummary("Search people to invite")
            .WithDescription("Owner only. Searches the application's participant directory; returns 404 when the application registered none.")
            .Produces<NhAssistantDirectoryEntryDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapPost("conversations/{id:guid}/participants", InviteAsync)
            .WithName("NhAssistantInviteParticipant")
            .WithSummary("Invite a person")
            .WithDescription("Owner only. Adds a person the participant directory resolves for the owner and notifies them.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapDelete("conversations/{id:guid}/participants/{actorId}", RemoveParticipantAsync)
            .WithName("NhAssistantRemoveParticipant")
            .WithSummary("Remove a participant or leave")
            .WithDescription("The owner removes a participant; a participant removes only themselves to leave.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapGet("notifications", GetNotificationSettingsAsync)
            .WithName("NhAssistantGetNotificationSettings")
            .WithSummary("Get my notification settings")
            .WithDescription("Returns whether the caller receives push notifications, whether the server supports them and the key browsers subscribe with.")
            .Produces<NhAssistantNotificationSettingsDto>();
        enabled.MapPut("notifications", UpdateNotificationSettingsAsync)
            .WithName("NhAssistantUpdateNotificationSettings")
            .WithSummary("Update my notification settings")
            .WithDescription("Turns push notifications on or off for every browser of the caller.")
            .Produces<NhAssistantNotificationSettingsDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
        enabled.MapPut("notifications/push-subscription", SubscribeAsync)
            .WithName("NhAssistantSubscribePush")
            .WithSummary("Register a browser for push notifications")
            .WithDescription("Stores the browser's push subscription for the caller. The endpoint must belong to an allowed push service.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapDelete("notifications/push-subscription", UnsubscribeAsync)
            .WithName("NhAssistantUnsubscribePush")
            .WithSummary("Unregister a browser")
            .WithDescription("Removes the caller's push subscription of one browser.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantCollaboration collaboration)
    {
        var resolved = await ResolveAccessAsync(httpContext, id);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantMarkReadRequest);
        await collaboration.MarkReadAsync(
            resolved.Access!,
            request?.Sequence ?? int.MaxValue,
            httpContext.RequestAborted);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> CreateShareLinkAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantCollaboration collaboration)
    {
        var resolved = await ResolveAccessAsync(httpContext, id, agentCheck: NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        var created = await collaboration.CreateShareLinkAsync(resolved.Access!, httpContext.RequestAborted);
        return created.Success
            ? Json(new NhAssistantShareLinkDto(created.Data!), NhAssistantJsonSerializerContext.Default.NhAssistantShareLinkDto)
            : FromFailure(created);
    }

    private static async Task<IResult> RevokeShareLinkAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantCollaboration collaboration)
    {
        var resolved = await ResolveAccessAsync(httpContext, id, agentCheck: NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        var revoked = await collaboration.RevokeShareLinkAsync(resolved.Access!, httpContext.RequestAborted);
        return revoked.Success ? TypedResults.NoContent() : FromFailure(revoked);
    }

    private static async Task<IResult> JoinAsync(
        Guid id,
        HttpContext httpContext,
        INhAssistantStore store,
        NhAssistantAgentCatalog registry,
        NhAssistantAgentAccess agentAccess,
        NhAssistantConversationReader reader,
        NhAssistantCollaboration collaboration,
        INhAssistantDisplayNameResolver displayNames)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantJoinConversationRequest);
        if (string.IsNullOrWhiteSpace(request?.Token))
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.ShareLinkInvalid);
        }

        // The agent policy is checked before anything about the conversation is revealed.
        var conversation = await store.FindShareableConversationAsync(id, httpContext.RequestAborted);
        var effective = conversation is null
            ? null
            : await registry.FindAsync(conversation.AgentId, includeDisabled: false, httpContext.RequestAborted);
        if (effective is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ShareLinkInvalid);
        }
        if (!await agentAccess.CanUseAsync(httpContext.User, effective.Definition))
        {
            return Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.AgentForbidden);
        }

        var displayName = await displayNames.GetDisplayNameAsync(httpContext.User, caller.Data, httpContext.RequestAborted);
        var joined = await collaboration.JoinAsync(
            id,
            caller.Data,
            request.Token,
            displayName ?? string.Empty,
            httpContext.RequestAborted);
        if (!joined.Success)
        {
            return FromFailure(joined);
        }
        return Json(
            await CreateConversationDtoAsync(joined.Data!, reader, collaboration, httpContext.RequestAborted),
            NhAssistantJsonSerializerContext.Default.NhAssistantConversationDto);
    }

    private static async Task<IResult> SearchCandidatesAsync(
        Guid id,
        string? query,
        HttpContext httpContext,
        INhAssistantStore store)
    {
        var directory = httpContext.RequestServices.GetService<INhAssistantParticipantDirectory>();
        if (directory is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.DirectoryUnavailable);
        }
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var resolved = await ResolveAccessAsync(httpContext, id, caller.Data, NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        if (!resolved.Access!.IsOwner)
        {
            return Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.OwnerRequired);
        }
        var text = query?.Trim();
        if (text is null || text.Length is < 2 or > 100)
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantAdminErrorCodes.ValidationFailed);
        }

        var members = (await store.GetParticipantsAsync(id, httpContext.RequestAborted))
            .Select(participant => participant.ActorId)
            .Append(resolved.Access.Conversation.OwnerActorId)
            .ToHashSet(StringComparer.Ordinal);
        var entries = await directory.SearchAsync(
            new NhAssistantDirectorySearch(caller.Data, text, MaxDirectoryResults),
            httpContext.RequestAborted);
        var candidates = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ActorId) && !members.Contains(entry.ActorId))
            .Take(MaxDirectoryResults)
            .Select(entry => new NhAssistantDirectoryEntryDto(
                entry.ActorId,
                NhAssistantParticipantNames.Normalize(entry.DisplayName),
                string.IsNullOrWhiteSpace(entry.Detail) ? null : NhAssistantParticipantNames.Normalize(entry.Detail)))
            .ToArray();
        return Json(candidates, NhAssistantJsonSerializerContext.Default.NhAssistantDirectoryEntryDtoArray);
    }

    private static async Task<IResult> InviteAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantCollaboration collaboration,
        INhAssistantDisplayNameResolver displayNames)
    {
        var directory = httpContext.RequestServices.GetService<INhAssistantParticipantDirectory>();
        if (directory is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.DirectoryUnavailable);
        }
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantInviteParticipantRequest);
        if (string.IsNullOrWhiteSpace(request?.ActorId) || request.ActorId.Length > 256)
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantAdminErrorCodes.ValidationFailed);
        }
        var resolved = await ResolveAccessAsync(httpContext, id, caller.Data, NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        if (!resolved.Access!.IsOwner)
        {
            return Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.OwnerRequired);
        }

        // The directory decides again who the owner may invite; a client-supplied id is never trusted.
        var invitee = await directory.FindAsync(
            new NhAssistantDirectoryLookup(caller.Data, request.ActorId),
            httpContext.RequestAborted);
        if (invitee is null || !string.Equals(invitee.ActorId, request.ActorId, StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ParticipantNotFound);
        }
        var ownerName = await displayNames.GetDisplayNameAsync(httpContext.User, caller.Data, httpContext.RequestAborted);
        var invited = await collaboration.InviteAsync(
            resolved.Access,
            invitee,
            ownerName ?? string.Empty,
            httpContext.RequestAborted);
        return invited.Success ? TypedResults.NoContent() : FromFailure(invited);
    }

    private static async Task<IResult> RemoveParticipantAsync(
        Guid id,
        string actorId,
        HttpContext httpContext,
        NhAssistantCollaboration collaboration)
    {
        var resolved = await ResolveAccessAsync(httpContext, id, agentCheck: NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        var removed = await collaboration.RemoveParticipantAsync(resolved.Access!, actorId, httpContext.RequestAborted);
        return removed.Success ? TypedResults.NoContent() : FromFailure(removed);
    }

    private static async Task<IResult> GetNotificationSettingsAsync(
        HttpContext httpContext,
        NhAssistantNotificationStore notifications,
        IOptionsMonitor<NhAssistantOptions> options)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var enabled = await notifications.IsPushEnabledAsync(caller.Data.ActorId, httpContext.RequestAborted);
        return Json(Settings(enabled, options.CurrentValue.Push), NhAssistantJsonSerializerContext.Default.NhAssistantNotificationSettingsDto);
    }

    private static async Task<IResult> UpdateNotificationSettingsAsync(
        HttpContext httpContext,
        NhAssistantNotificationStore notifications,
        IOptionsMonitor<NhAssistantOptions> options)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantUpdateNotificationSettingsRequest);
        if (request?.PushEnabled is not { } enabled)
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantAdminErrorCodes.ValidationFailed);
        }
        await notifications.SetPushEnabledAsync(caller.Data.ActorId, enabled, httpContext.RequestAborted);
        return Json(Settings(enabled, options.CurrentValue.Push), NhAssistantJsonSerializerContext.Default.NhAssistantNotificationSettingsDto);
    }

    private static async Task<IResult> SubscribeAsync(
        HttpContext httpContext,
        NhAssistantNotificationStore notifications,
        IOptionsMonitor<NhAssistantOptions> options)
    {
        var push = options.CurrentValue.Push;
        if (!push.IsConfigured)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.PushUnavailable);
        }
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantPushSubscriptionRequest);
        var subscription = NhAssistantPushSubscriptionValidator.Validate(request, push);
        if (subscription is null)
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.PushSubscriptionInvalid);
        }
        await notifications.UpsertSubscriptionAsync(
            caller.Data.ActorId,
            subscription.Endpoint,
            subscription.P256dh,
            subscription.Auth,
            subscription.Language,
            push.MaxSubscriptionsPerActor,
            httpContext.RequestAborted);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UnsubscribeAsync(
        HttpContext httpContext,
        NhAssistantNotificationStore notifications)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantRemovePushSubscriptionRequest);
        if (string.IsNullOrWhiteSpace(request?.Endpoint) || request.Endpoint.Length > NhAssistantPushLimits.MaxEndpointLength)
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.PushSubscriptionInvalid);
        }
        await notifications.DeleteSubscriptionAsync(caller.Data.ActorId, request.Endpoint, httpContext.RequestAborted);
        return TypedResults.NoContent();
    }

    private static NhAssistantNotificationSettingsDto Settings(bool enabled, NhAssistantPushOptions push)
    {
        return new NhAssistantNotificationSettingsDto(
            enabled,
            push.IsConfigured,
            push.IsConfigured ? push.PublicKey : null);
    }
}
