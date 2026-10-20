using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Live;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

public static class NhAssistantEndpointRouteBuilderExtensions
{
    public const string DefaultPrefix = "/api/assistant";

    /// <summary>
    /// Maps the assistant HTTP API: status, agents, conversations, messages as server-sent events,
    /// approval decisions, cancel, sharing, read state and notification settings, plus the SignalR
    /// hub for live updates at <see cref="NhAssistantOptions.HubPath"/>. Every endpoint requires the
    /// assistant access policy; when <c>NewHeap:AI:Assistant:Enabled</c> is false, <c>GET status</c>
    /// reports the assistant as disabled and every other endpoint returns <c>404</c>.
    /// </summary>
    public static RouteGroupBuilder MapNewHeapAssistant(
        this IEndpointRouteBuilder endpoints,
        string prefix = DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var state = endpoints.ServiceProvider.GetService<NhAssistantRegistrationState>()
            ?? throw new InvalidOperationException("Call AddNewHeapAssistant before MapNewHeapAssistant.");
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<NhAssistantOptions>>().Value;
        var accessPolicy = NhAssistantEndpointOptions.ResolveAccessPolicy(state, options);

        var group = endpoints.MapGroup(prefix)
            .RequireAuthorization(accessPolicy)
            .WithTags("Assistant")
            .AddEndpointFilter(NhAssistantRequestCulture.MatchLanguageAsync);

        group.MapGet("status", GetStatusAsync)
            .WithName("NhAssistantGetStatus")
            .WithSummary("Get assistant status")
            .WithDescription("Returns whether the assistant is enabled, the agents the caller may use, the message limits and the collaboration features.")
            .Produces<NhAssistantStatusDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var enabled = group.MapGroup(string.Empty).AddEndpointFilter(RequireEnabledAsync);
        enabled.MapGet("agents", GetAgentsAsync)
            .WithName("NhAssistantGetAgents")
            .WithSummary("List assistant agents")
            .WithDescription("Returns the agents whose required policy the caller satisfies.")
            .Produces<NhAssistantAgentSummaryDto[]>()
            .Produces(StatusCodes.Status404NotFound);
        enabled.MapGet("conversations", ListConversationsAsync)
            .WithName("NhAssistantListConversations")
            .WithSummary("List conversations")
            .WithDescription("Returns the conversations the caller owns or was invited to, most recently updated first, with the caller's read state.")
            .Produces<NhAssistantConversationListDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
        enabled.MapPost("conversations", CreateConversationAsync)
            .WithName("NhAssistantCreateConversation")
            .WithSummary("Create a conversation")
            .WithDescription("Starts an empty conversation with an agent the caller may use.")
            .Produces<NhAssistantConversationDto>(StatusCodes.Status201Created)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapGet("conversations/{id:guid}", GetConversationAsync)
            .WithName("NhAssistantGetConversation")
            .WithSummary("Get a conversation")
            .WithDescription("Returns a conversation the caller owns or participates in, with its messages, members and pending approval. For an agent thread it also returns the subject, the caller's role and whether the caller may decide each open proposal.")
            .Produces<NhAssistantConversationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapDelete("conversations/{id:guid}", DeleteConversationAsync)
            .WithName("NhAssistantDeleteConversation")
            .WithSummary("Delete or leave a conversation")
            .WithDescription("The owner archives the conversation for everyone; a participant leaves it. It no longer appears for them.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapPost("conversations/{id:guid}/messages", SendMessageAsync)
            .WithName("NhAssistantSendMessage")
            .WithSummary("Send a message")
            .WithDescription("Starts a turn as the caller and streams it as server-sent events. Returns 409 when the conversation is not idle.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapPost("conversations/{id:guid}/approvals/{approvalId:guid}/decide", DecideAsync)
            .WithName("NhAssistantDecideApproval")
            .WithSummary("Decide an approval")
            .WithDescription("Approves or rejects the pending proposal bound to the expected proposal hash and streams the resumed turn. Only the person whose turn created the proposal decides.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapPost("conversations/{id:guid}/cancel", CancelAsync)
            .WithName("NhAssistantCancelTurn")
            .WithSummary("Cancel a turn")
            .WithDescription("Cancels the running turn in this process or dismisses a pending approval. Only the owner or the person who started the turn may cancel it.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        NhAssistantCollaborationEndpoints.Map(enabled);
        AgentRuns.NhAssistantAgentRunEndpoints.Map(enabled);
        AgentRuns.NhAssistantProposalFeedbackEndpoints.Map(enabled);
        AgentRuns.NhAssistantSubjectEndpoints.Map(enabled);
        AgentRuns.NhAssistantRunHistoryEndpoints.Map(enabled);
        AgentRuns.NhAssistantBoardEndpoints.Map(enabled);

        // Webhook triggers carry no user: they are authenticated by their signature.
        var webhooks = endpoints.MapGroup(prefix)
            .WithTags("Assistant")
            .AddEndpointFilter(RequireEnabledAsync);
        AgentRuns.NhAssistantWebhookEndpoints.Map(webhooks);
        // The OAuth callback carries no session either: it is bound to its flow by the state.
        NhAssistantMcpOAuthEndpoints.Map(webhooks);
        NhAssistantAdminEndpoints.Map(enabled, NhAssistantEndpointOptions.ResolveAdminPolicy(state, options));
        NhAssistantMcpConnectionEndpoints.Map(enabled);

        if (!string.IsNullOrWhiteSpace(options.HubPath))
        {
            endpoints.MapHub<NhAssistantHub>(options.HubPath)
                .RequireAuthorization(accessPolicy);
        }
        return group;
    }

    private static async ValueTask<object?> RequireEnabledAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<NhAssistantOptions>>()
            .CurrentValue;
        if (!options.Enabled)
        {
            return Error(NhAssistantErrorCodes.Disabled);
        }
        return await next(context);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext httpContext,
        IOptionsMonitor<NhAssistantOptions> options,
        NhAssistantRegistrationState state,
        NhAssistantAgentAccess access)
    {
        var limits = new NhAssistantLimitsDto(state.Limits.MaxMessageChars, state.Limits.MaxToolCallsPerTurn);
        var current = options.CurrentValue;
        if (!current.Enabled)
        {
            return Json(new NhAssistantStatusDto(false, [], limits, false), NhAssistantJsonSerializerContext.Default.NhAssistantStatusDto);
        }
        var agents = await access.GetVisibleAgentsAsync(httpContext.User, httpContext.RequestAborted);
        var canAdminister = await access.CanAdministerAsync(httpContext.User);
        // Checks the registration without constructing the application's directory.
        var directory = httpContext.RequestServices.GetService<IServiceProviderIsService>()
            ?.IsService(typeof(INhAssistantParticipantDirectory)) == true;
        var collaboration = new NhAssistantCollaborationDto(
            directory,
            string.IsNullOrWhiteSpace(current.HubPath) ? null : current.HubPath,
            current.Push.IsConfigured,
            state.Limits.MaxParticipantsPerConversation);
        return Json(
            new NhAssistantStatusDto(true, agents.Select(NhAssistantDtoMapper.ToDto).ToArray(), limits, canAdminister, collaboration),
            NhAssistantJsonSerializerContext.Default.NhAssistantStatusDto);
    }

    private static async Task<IResult> GetAgentsAsync(HttpContext httpContext, NhAssistantAgentAccess access)
    {
        var agents = await access.GetVisibleAgentsAsync(httpContext.User, httpContext.RequestAborted);
        return Json(
            agents.Select(NhAssistantDtoMapper.ToDto).ToArray(),
            NhAssistantJsonSerializerContext.Default.NhAssistantAgentSummaryDtoArray);
    }

    private static async Task<IResult> ListConversationsAsync(
        HttpContext httpContext,
        INhAssistantStore store,
        int? page,
        int? itemsPerPage)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var pageNumber = page ?? 1;
        var size = itemsPerPage ?? 20;
        if (pageNumber < 1 || size is < 1 or > 100)
        {
            return Error(NhAssistantErrorCodes.MessageInvalid);
        }
        var (items, total) = await store.ListConversationsAsync(
            caller.Data.ActorId,
            caller.Data.TenantId,
            pageNumber,
            size,
            httpContext.RequestAborted);
        return Json(
            new NhAssistantConversationListDto(items.Select(NhAssistantDtoMapper.ToSummary).ToArray(), total),
            NhAssistantJsonSerializerContext.Default.NhAssistantConversationListDto);
    }

    private static async Task<IResult> CreateConversationAsync(
        HttpContext httpContext,
        INhAssistantStore store,
        NhAssistantAgentCatalog registry,
        NhAssistantAgentAccess access,
        NhAssistantConversationReader reader,
        INhAssistantDisplayNameResolver displayNames)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantCreateConversationRequest);
        if (request is null || string.IsNullOrWhiteSpace(request.AgentId))
        {
            return Error(NhAssistantErrorCodes.AgentNotFound);
        }
        var title = request.Title?.Trim();
        if (title is { Length: > 200 })
        {
            return Error(NhAssistantErrorCodes.TitleInvalid);
        }
        var effective = await registry.FindAsync(request.AgentId, includeDisabled: false, httpContext.RequestAborted);
        // An agent kept out of the chat reads as unknown here; its runs and subject threads go on.
        if (effective is null || !effective.OfferedInChat)
        {
            return Error(NhAssistantErrorCodes.AgentNotFound);
        }
        var agent = effective.Definition;
        if (!await access.CanUseAsync(httpContext.User, agent))
        {
            return Error(NhAssistantErrorCodes.AgentForbidden);
        }

        var ownerName = NhAssistantParticipantNames.Normalize(
            await displayNames.GetDisplayNameAsync(httpContext.User, caller.Data, httpContext.RequestAborted));
        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = caller.Data.ActorId,
            OwnerDisplayName = ownerName.Length == 0 ? null : ownerName,
            TenantId = caller.Data.TenantId,
            AgentId = agent.Id,
            AgentVersion = agent.Version,
            Title = string.IsNullOrWhiteSpace(title) ? null : title,
            Status = NhAssistantConversationStatuses.Idle,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await store.AddConversationAsync(conversation, httpContext.RequestAborted);
        var view = await reader.CreateViewAsync(conversation, httpContext.RequestAborted);
        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.Value?.TrimEnd('/')}/{conversation.Id}";
        return new CreatedJson<NhAssistantConversationDto>(
            location,
            NhAssistantDtoMapper.ToDto(view, caller.Data.ActorId),
            NhAssistantJsonSerializerContext.Default.NhAssistantConversationDto);
    }

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantConversationReader reader,
        NhAssistantCollaboration collaboration)
    {
        var resolved = await ResolveAccessAsync(httpContext, id);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        return Json(
            await CreateConversationDtoAsync(resolved.Access!, reader, collaboration, httpContext.RequestAborted, httpContext),
            NhAssistantJsonSerializerContext.Default.NhAssistantConversationDto);
    }

    private static async Task<IResult> DeleteConversationAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantCollaboration collaboration)
    {
        var resolved = await ResolveAccessAsync(httpContext, id, agentCheck: NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        var deleted = await collaboration.DeleteAsync(resolved.Access!, httpContext.RequestAborted);
        return deleted.Success ? TypedResults.NoContent() : FromFailure(deleted);
    }

    private static async Task<IResult> SendMessageAsync(
        Guid id,
        HttpContext httpContext,
        INhAssistantTurnRunner runner,
        NhAssistantCollaboration collaboration,
        INhAssistantDisplayNameResolver displayNames)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantSendMessageRequest);
        if (request is null)
        {
            return Error(NhAssistantErrorCodes.MessageInvalid);
        }
        var resolved = await ResolveAccessAsync(httpContext, id, caller.Data, NhAssistantAgentCheck.Everyone);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        // Other people in a shared conversation see the sender's current name.
        await collaboration.RefreshDisplayNameAsync(
            resolved.Access!,
            await displayNames.GetDisplayNameAsync(httpContext.User, caller.Data, httpContext.RequestAborted),
            httpContext.RequestAborted);

        var threadAccess = ThreadAccessOf(httpContext);
        NhAssistantAgentTurnContext? agentTurn = null;
        if (threadAccess is not null)
        {
            if (threadAccess.Role < NhAiSubjectRole.Participant)
            {
                return Error(NhAssistantErrorCodes.SubjectAccessDenied);
            }

            var created = await httpContext.RequestServices.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
                id,
                null,
                NhAssistantConversationKinds.Chat,
                caller.Data.ActorId,
                threadAccess.Thread.AccountableOwnerId,
                httpContext.RequestAborted);
            if (!created.Success)
            {
                return FromFailure(created);
            }
            agentTurn = created.Data! with { IsRequestTurn = true };
        }

        var text = request.Text ?? string.Empty;
        var started = await runner.StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(
                id,
                caller.Data,
                text,
                request.ClientMessageId,
                httpContext.RequestAborted,
                RequestLanguage(httpContext),
                NhAssistantClientContext.From(request.ClientContext))
            {
                AgentTurn = agentTurn
            },
            httpContext.RequestAborted);
        if (!started.Success
            && agentTurn is not null
            && started.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.ConversationBusy)
            && !string.IsNullOrWhiteSpace(text))
        {
            // A run owns the thread; the message waits and the agent reads it in its next turn.
            var triggers = httpContext.RequestServices.GetRequiredService<NhAssistantAgentTriggers>();
            var receipt = await triggers.FireAsync(
                new NhAssistantTriggerFire(
                    NhAssistantTriggerIds.QueuedMessage,
                    agentTurn.Thread.AgentId,
                    agentTurn.Subject.Subject,
                    caller.Data.ActorId,
                    text.Trim(),
                    null,
                    NhAssistantTriggerConflict.Coalesce,
                    TimeSpan.Zero),
                httpContext.RequestAborted);
            return TypedResults.Json(
                new NhAssistantTriggerReceiptDto(receipt.TriggerId, receipt.Outcome, receipt.RunId, receipt.ConversationId, receipt.ReasonCode),
                NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantTriggerReceiptDto,
                statusCode: StatusCodes.Status202Accepted);
        }

        return started.Success
            ? new NhAssistantServerSentEventsResult(started.Data)
            : FromFailure(started);
    }

    private static async Task<IResult> DecideAsync(
        Guid id,
        Guid approvalId,
        HttpContext httpContext,
        INhAssistantTurnRunner runner)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantDecideApprovalRequest);
        if (request is null
            || request.Decision is not ("approve" or "reject")
            || string.IsNullOrWhiteSpace(request.ExpectedProposalHash))
        {
            return Error(NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }
        var resolved = await ResolveAccessAsync(httpContext, id, caller.Data, NhAssistantAgentCheck.Everyone);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }

        var threadAccess = ThreadAccessOf(httpContext);
        NhAssistantAgentTurnContext? agentTurn = null;
        if (threadAccess is not null)
        {
            var services = httpContext.RequestServices;
            var approval = await services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approvalId, httpContext.RequestAborted);
            if (approval is null || approval.ConversationId != id)
            {
                return Error(NhAssistantErrorCodes.ApprovalNotFound);
            }
            if (approval.RunId is not null)
            {
                // A run's proposal is decided through approvals/{approvalId}/decisions.
                return Error(NhAssistantErrorCodes.ApprovalDecisionInvalid);
            }
            var check = await services.GetRequiredService<NhAssistantApproverCheck>().CheckAsync(
                approval,
                threadAccess.Thread,
                caller.Data.ActorId,
                httpContext.User,
                httpContext.RequestAborted);
            if (!check.Success)
            {
                return Error(NhAssistantErrorCodes.ApprovalPolicyDenied);
            }

            var created = await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
                id,
                null,
                NhAssistantConversationKinds.Chat,
                approval.InitiatorActorId ?? caller.Data.ActorId,
                threadAccess.Thread.AccountableOwnerId,
                httpContext.RequestAborted);
            if (!created.Success)
            {
                return FromFailure(created);
            }
            agentTurn = created.Data! with { IsRequestTurn = true };
        }

        var started = await runner.StartDecisionTurnAsync(
            new NhAssistantDecisionTurnRequest(
                id,
                caller.Data,
                approvalId,
                request.Decision == "approve",
                request.ExpectedProposalHash,
                string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                httpContext.RequestAborted,
                RequestLanguage(httpContext))
            {
                AgentTurn = agentTurn,
                PolicyAuthorized = agentTurn is not null
            },
            httpContext.RequestAborted);
        return started.Success
            ? new NhAssistantServerSentEventsResult(started.Data)
            : FromFailure(started);
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        HttpContext httpContext,
        INhAssistantTurnRunner runner)
    {
        var resolved = await ResolveAccessAsync(httpContext, id, agentCheck: NhAssistantAgentCheck.None);
        if (resolved.Error is not null)
        {
            return resolved.Error;
        }
        var access = resolved.Access!;
        if (ThreadAccessOf(httpContext) is { } threadAccess)
        {
            if (threadAccess.Role < NhAiSubjectRole.Participant)
            {
                return Error(NhAssistantErrorCodes.SubjectAccessDenied);
            }

            // In an agent thread every participant of the subject may stop a turn.
            access = access with { Participant = null };
        }
        var cancelled = await runner.CancelAsync(access, httpContext.RequestAborted);
        return cancelled.Success
            ? TypedResults.Accepted((string?)null)
            : FromFailure(cancelled);
    }

    internal static async Task<NhAssistantConversationDto> CreateConversationDtoAsync(
        NhAssistantConversationAccess access,
        NhAssistantConversationReader reader,
        NhAssistantCollaboration collaboration,
        CancellationToken cancellationToken,
        HttpContext? httpContext = null)
    {
        var view = await reader.CreateViewAsync(access, cancellationToken);
        NhAssistantConversationThreadDto? thread = null;
        if (httpContext is not null && ThreadAccessOf(httpContext) is { } threadAccess)
        {
            view = await AgentRuns.NhAssistantRestrictedResults.ApplyAsync(view, httpContext, threadAccess);
            thread = await AgentRuns.NhAssistantThreadStanding.CreateAsync(
                httpContext.RequestServices,
                httpContext.User,
                threadAccess,
                access.ActorId,
                cancellationToken);
        }

        return NhAssistantDtoMapper.ToDto(
            view,
            access.ActorId,
            access.IsOwner ? collaboration.GetShareToken(view.ProtectedShareToken) : null,
            thread);
    }

    /// <summary>
    /// Resolves the caller's access to a conversation and checks the agent's policy as
    /// <paramref name="agentCheck"/> asks: a participant must satisfy it to read, so revoked
    /// permissions take effect immediately, while everyone is checked again before a turn starts.
    /// </summary>
    internal static async Task<(NhAssistantConversationAccess? Access, IResult? Error)> ResolveAccessAsync(
        HttpContext httpContext,
        Guid conversationId,
        NhAiInvocationContext? caller = null,
        NhAssistantAgentCheck agentCheck = NhAssistantAgentCheck.Participants)
    {
        if (caller is null)
        {
            var resolvedCaller = await ResolveCallerAsync(httpContext);
            if (!resolvedCaller.Success)
            {
                return (null, ContextUnavailable());
            }
            caller = resolvedCaller.Data;
        }

        var services = httpContext.RequestServices;
        var thread = await services.GetRequiredService<NhAssistantRunStore>()
            .FindThreadByConversationAsync(conversationId, httpContext.RequestAborted);
        if (thread is not null)
        {
            // A hidden shadow thread answers like one that does not exist, before access records
            // the caller as a participant.
            if (await IsHiddenShadowThreadAsync(httpContext, thread))
            {
                return (null, Error(NhAssistantErrorCodes.ConversationNotFound));
            }

            // Access to an agent thread follows the subject; the participant row holds read state.
            var displayName = await services.GetRequiredService<INhAssistantDisplayNameResolver>()
                .GetDisplayNameAsync(httpContext.User, caller, httpContext.RequestAborted);
            var threadAccess = await services.GetRequiredService<NhAssistantThreadService>()
                .ResolveAccessAsync(conversationId, caller.ActorId, displayName, httpContext.RequestAborted);
            if (threadAccess is null)
            {
                return (null, Error(NhAssistantErrorCodes.ConversationNotFound));
            }
            httpContext.Items[ThreadAccessItemKey] = threadAccess;
        }

        var store = services.GetRequiredService<INhAssistantStore>();
        var access = await store.FindAccessAsync(conversationId, caller.ActorId, caller.TenantId, httpContext.RequestAborted);
        if (access is null)
        {
            return (null, Error(NhAssistantErrorCodes.ConversationNotFound));
        }
        if (agentCheck == NhAssistantAgentCheck.None
            || (agentCheck == NhAssistantAgentCheck.Participants && access.IsOwner))
        {
            return (access, null);
        }

        var registry = services.GetRequiredService<NhAssistantAgentCatalog>();
        var effective = await registry.FindAsync(access.Conversation.AgentId, includeDisabled: false, httpContext.RequestAborted);
        if (effective is null)
        {
            return (null, Error(NhAssistantErrorCodes.AgentNotFound));
        }
        var agentAccess = services.GetRequiredService<NhAssistantAgentAccess>();
        return await agentAccess.CanUseAsync(httpContext.User, effective.Definition)
            ? (access, null)
            : (null, Error(NhAssistantErrorCodes.AgentForbidden));
    }

    private const string ThreadAccessItemKey = "NhAssistant.ThreadAccess";

    /// <summary>
    /// Shadow threads are visible to administrators only: nobody else hears about them, and they
    /// must not act on proposals that never execute. Every thread of a shadow variant is one.
    /// </summary>
    internal static async Task<bool> IsHiddenShadowThreadAsync(HttpContext httpContext, AssistantThread thread)
    {
        var registration = httpContext.RequestServices.GetRequiredService<NhAssistantRegistrationState>();
        var shadow = thread.Shadow
            || (registration.AgentRuns.TryGetValue(thread.AgentId, out var runOptions) && runOptions.ShadowOfAgentId is not null);
        return shadow && !await IsAdministratorAsync(httpContext);
    }

    /// <summary>
    /// Whether the agent is hidden from the caller before any thread: a shadow variant works for
    /// administrators only.
    /// </summary>
    internal static async Task<bool> IsHiddenVariantAsync(HttpContext httpContext, string agentId)
    {
        var registration = httpContext.RequestServices.GetRequiredService<NhAssistantRegistrationState>();
        return registration.AgentRuns.TryGetValue(agentId, out var runOptions)
            && runOptions.ShadowOfAgentId is not null
            && !await IsAdministratorAsync(httpContext);
    }

    internal static async Task<bool> IsAdministratorAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var adminPolicy = NhAssistantEndpointOptions.ResolveAdminPolicy(
            services.GetRequiredService<NhAssistantRegistrationState>(),
            services.GetRequiredService<IOptions<NhAssistantOptions>>().Value);
        var authorization = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(httpContext.User, adminPolicy);
        return authorization.Succeeded;
    }

    /// <summary>
    /// The caller's live access to the agent thread resolved for this request, or
    /// <see langword="null"/> for a chat conversation.
    /// </summary>
    internal static NhAssistantThreadAccess? ThreadAccessOf(HttpContext httpContext)
    {
        return httpContext.Items.TryGetValue(ThreadAccessItemKey, out var value)
            ? value as NhAssistantThreadAccess
            : null;
    }

    internal static async Task<TaskResult<NhAiInvocationContext>> ResolveCallerAsync(HttpContext httpContext)
    {
        return await httpContext.RequestServices
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, httpContext.RequestAborted);
    }

    internal static async Task<T?> ReadBodyAsync<T>(HttpContext httpContext, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!httpContext.Request.HasJsonContentType())
        {
            return null;
        }
        try
        {
            return await httpContext.Request.ReadFromJsonAsync(typeInfo, httpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The error response of an expected failure: its code with the one status the assistant gives
    /// it (<see cref="NhAssistantHttpErrors"/>) and its field errors.
    /// </summary>
    internal static IResult FromFailure(TaskResult result)
    {
        return NhAssistantHttpErrors.Failure(result);
    }

    /// <summary>
    /// The preference language: <c>nl</c> when the preferred <c>Accept-Language</c> is Dutch, otherwise <c>en</c>.
    /// </summary>
    internal static string RequestLanguage(HttpContext httpContext)
    {
        var preferred = httpContext.Request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(item => item.Quality ?? 1)
            .Select(item => item.Value.Value)
            .FirstOrDefault();
        return preferred is not null && preferred.StartsWith("nl", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";
    }

    internal static IResult ContextUnavailable()
    {
        return Error(NhAssistantErrorCodes.ContextUnavailable);
    }

    /// <summary>
    /// The error response of a code with the one status the assistant gives it.
    /// </summary>
    internal static IResult Error(string code)
    {
        return NhAssistantHttpErrors.Error(code);
    }

    internal static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return TypedResults.Json(value, typeInfo);
    }

    /// <summary>
    /// A <c>201 Created</c> response serialized with the assistant JSON contract.
    /// </summary>
    private sealed class CreatedJson<T>(string location, T value, JsonTypeInfo<T> typeInfo) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status201Created;
            httpContext.Response.Headers.Location = location;
            return httpContext.Response.WriteAsJsonAsync(value, typeInfo, cancellationToken: httpContext.RequestAborted);
        }
    }
}

/// <summary>
/// Who must satisfy the agent's required policy for a conversation request.
/// </summary>
internal enum NhAssistantAgentCheck
{
    /// <summary>
    /// Nobody: deleting, leaving and cancelling stay possible after a permission was revoked.
    /// </summary>
    None = 0,

    /// <summary>
    /// Participants only: reading a shared conversation.
    /// </summary>
    Participants = 1,

    /// <summary>
    /// Owner and participants: starting or resuming a turn.
    /// </summary>
    Everyone = 2
}
