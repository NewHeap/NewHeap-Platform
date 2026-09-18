using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet;

public static class NhAssistantEndpointRouteBuilderExtensions
{
    public const string DefaultPrefix = "/api/assistant";

    /// <summary>
    /// Maps the assistant HTTP API: status, agents, conversations, messages as server-sent events,
    /// approval decisions and cancel. Every endpoint requires the assistant access policy; when
    /// <c>NewHeap:AI:Assistant:Enabled</c> is false, <c>GET status</c> reports the assistant as disabled
    /// and every other endpoint returns <c>404</c>.
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
            .WithTags("Assistant");

        group.MapGet("status", GetStatusAsync)
            .WithName("NhAssistantGetStatus")
            .WithSummary("Get assistant status")
            .WithDescription("Returns whether the assistant is enabled, the agents the caller may use and the message limits.")
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
            .WithDescription("Returns the caller's conversations, most recently updated first.")
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
            .WithDescription("Returns one of the caller's conversations with its messages and pending approval.")
            .Produces<NhAssistantConversationDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapDelete("conversations/{id:guid}", DeleteConversationAsync)
            .WithName("NhAssistantDeleteConversation")
            .WithSummary("Delete a conversation")
            .WithDescription("Archives one of the caller's conversations; it no longer appears in the API.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapPost("conversations/{id:guid}/messages", SendMessageAsync)
            .WithName("NhAssistantSendMessage")
            .WithSummary("Send a message")
            .WithDescription("Starts a turn and streams it as server-sent events. Returns 409 when the conversation is not idle.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapPost("conversations/{id:guid}/approvals/{approvalId:guid}/decide", DecideAsync)
            .WithName("NhAssistantDecideApproval")
            .WithSummary("Decide an approval")
            .WithDescription("Approves or rejects the pending proposal bound to the expected proposal hash and streams the resumed turn.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
        enabled.MapPost("conversations/{id:guid}/cancel", CancelAsync)
            .WithName("NhAssistantCancelTurn")
            .WithSummary("Cancel a turn")
            .WithDescription("Cancels the running turn in this process or dismisses a pending approval.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
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
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.Disabled);
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
        if (!options.CurrentValue.Enabled)
        {
            return Json(new NhAssistantStatusDto(false, [], limits), NhAssistantJsonSerializerContext.Default.NhAssistantStatusDto);
        }
        var agents = await access.GetVisibleAgentsAsync(httpContext.User, httpContext.RequestAborted);
        return Json(
            new NhAssistantStatusDto(true, agents.Select(NhAssistantDtoMapper.ToDto).ToArray(), limits),
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
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }
        var (items, total) = await store.ListConversationsAsync(
            caller.Data.ActorId,
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
        NhAssistantConversationReader reader)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantCreateConversationRequest);
        if (request is null || string.IsNullOrWhiteSpace(request.AgentId))
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.AgentNotFound);
        }
        var title = request.Title?.Trim();
        if (title is { Length: > 200 })
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.TitleInvalid);
        }
        var effective = await registry.FindAsync(request.AgentId, includeDisabled: false, httpContext.RequestAborted);
        if (effective is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.AgentNotFound);
        }
        var agent = effective.Definition;
        if (!await access.CanUseAsync(httpContext.User, agent))
        {
            return Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.AgentForbidden);
        }

        var now = DateTimeOffset.UtcNow;
        var conversation = new AssistantConversation
        {
            Id = Guid.NewGuid(),
            OwnerActorId = caller.Data.ActorId,
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
            NhAssistantDtoMapper.ToDto(view),
            NhAssistantJsonSerializerContext.Default.NhAssistantConversationDto);
    }

    private static async Task<IResult> GetConversationAsync(
        Guid id,
        HttpContext httpContext,
        NhAssistantConversationReader reader)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var view = await reader.GetAsync(id, caller.Data.ActorId, httpContext.RequestAborted);
        return view is null
            ? Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ConversationNotFound)
            : Json(NhAssistantDtoMapper.ToDto(view), NhAssistantJsonSerializerContext.Default.NhAssistantConversationDto);
    }

    private static async Task<IResult> DeleteConversationAsync(
        Guid id,
        HttpContext httpContext,
        INhAssistantStore store)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        if (await store.TryArchiveConversationAsync(id, caller.Data.ActorId, httpContext.RequestAborted))
        {
            return TypedResults.NoContent();
        }
        var existing = await store.FindConversationAsync(id, caller.Data.ActorId, httpContext.RequestAborted);
        return existing is null
            ? Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ConversationNotFound)
            : Error(StatusCodes.Status409Conflict, NhAssistantErrorCodes.ConversationBusy);
    }

    private static async Task<IResult> SendMessageAsync(
        Guid id,
        HttpContext httpContext,
        INhAssistantStore store,
        NhAssistantAgentCatalog registry,
        NhAssistantAgentAccess access,
        INhAssistantTurnRunner runner)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        var request = await ReadBodyAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantSendMessageRequest);
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.MessageInvalid);
        }
        var forbidden = await CheckAgentAccessAsync(httpContext, id, caller.Data.ActorId, store, registry, access);
        if (forbidden is not null)
        {
            return forbidden;
        }

        var started = await runner.StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(
                id,
                caller.Data,
                request.Text ?? string.Empty,
                request.ClientMessageId,
                httpContext.RequestAborted),
            httpContext.RequestAborted);
        return started.Success
            ? new NhAssistantServerSentEventsResult(started.Data)
            : FromTurnFailure(started);
    }

    private static async Task<IResult> DecideAsync(
        Guid id,
        Guid approvalId,
        HttpContext httpContext,
        INhAssistantStore store,
        NhAssistantAgentCatalog registry,
        NhAssistantAgentAccess access,
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
            return Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.ApprovalDecisionInvalid);
        }
        var forbidden = await CheckAgentAccessAsync(httpContext, id, caller.Data.ActorId, store, registry, access);
        if (forbidden is not null)
        {
            return forbidden;
        }

        var started = await runner.StartDecisionTurnAsync(
            new NhAssistantDecisionTurnRequest(
                id,
                caller.Data,
                approvalId,
                request.Decision == "approve",
                request.ExpectedProposalHash,
                string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                httpContext.RequestAborted),
            httpContext.RequestAborted);
        return started.Success
            ? new NhAssistantServerSentEventsResult(started.Data)
            : FromTurnFailure(started);
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        HttpContext httpContext,
        INhAssistantStore store,
        INhAssistantTurnRunner runner)
    {
        var caller = await ResolveCallerAsync(httpContext);
        if (!caller.Success)
        {
            return ContextUnavailable();
        }
        if (await store.FindConversationAsync(id, caller.Data.ActorId, httpContext.RequestAborted) is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ConversationNotFound);
        }
        await runner.CancelAsync(id, caller.Data.ActorId, httpContext.RequestAborted);
        return TypedResults.Accepted((string?)null);
    }

    /// <summary>
    /// Re-checks the agent policy on every turn so revoked permissions take effect immediately.
    /// </summary>
    private static async Task<IResult?> CheckAgentAccessAsync(
        HttpContext httpContext,
        Guid conversationId,
        string ownerActorId,
        INhAssistantStore store,
        NhAssistantAgentCatalog registry,
        NhAssistantAgentAccess access)
    {
        var conversation = await store.FindConversationAsync(conversationId, ownerActorId, httpContext.RequestAborted);
        if (conversation is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.ConversationNotFound);
        }
        var effective = await registry.FindAsync(conversation.AgentId, includeDisabled: false, httpContext.RequestAborted);
        if (effective is null)
        {
            return Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.AgentNotFound);
        }
        var agent = effective.Definition;
        return await access.CanUseAsync(httpContext.User, agent)
            ? null
            : Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.AgentForbidden);
    }

    private static async Task<TaskResult<NhAiInvocationContext>> ResolveCallerAsync(HttpContext httpContext)
    {
        return await httpContext.RequestServices
            .GetRequiredService<INhAiAuthenticatedInvocationContextResolver>()
            .ResolveAsync(httpContext, httpContext.RequestAborted);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext httpContext, JsonTypeInfo<T> typeInfo)
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

    private static IResult FromTurnFailure(TaskResult result)
    {
        var code = result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? NhAssistantErrorCodes.TurnFailed;
        var status = code switch
        {
            NhAssistantErrorCodes.ConversationNotFound
                or NhAssistantErrorCodes.ApprovalNotFound
                or NhAssistantErrorCodes.AgentNotFound => StatusCodes.Status404NotFound,
            NhAssistantErrorCodes.ConversationBusy
                or NhAssistantErrorCodes.MessageDuplicate
                or NhAssistantErrorCodes.ApprovalNotPending
                or NhAssistantErrorCodes.ProposalHashMismatch => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Error(status, code);
    }

    private static IResult ContextUnavailable()
    {
        return Error(StatusCodes.Status403Forbidden, NhAssistantErrorCodes.ContextUnavailable);
    }

    private static IResult Error(int statusCode, string code)
    {
        return TypedResults.Json(
            new NhAssistantErrorDto(code, NhAssistantErrorCodes.MessageKey(code)),
            NhAssistantJsonSerializerContext.Default.NhAssistantErrorDto,
            statusCode: statusCode);
    }

    private static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo)
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
