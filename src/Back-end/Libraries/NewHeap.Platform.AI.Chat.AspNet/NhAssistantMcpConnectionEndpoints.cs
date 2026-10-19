using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The caller's own decisions about MCP servers: consent to background use of servers that run with
/// the caller's credential.
/// </summary>
internal static class NhAssistantMcpConnectionEndpoints
{
    public static void Map(RouteGroupBuilder enabled)
    {
        enabled.MapGet("mcp-connections", GetConnectionsAsync)
            .WithName("NhAssistantGetMcpConnections")
            .WithSummary("List my MCP servers for background runs")
            .WithDescription("Returns the MCP servers that agents may use in background runs with the caller's own credential, after the caller's consent: servers that an administrator allows for personal background use, assigned to an agent the caller may use and whose required policy the caller passes.")
            .Produces<NhAssistantMcpConnectionDto[]>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
        enabled.MapPut("mcp-connections/{serverId}", PutConnectionAsync)
            .WithName("NhAssistantUpdateMcpConnection")
            .WithSummary("Allow or withdraw background use of an MCP server")
            .WithDescription("Records the caller's consent to background use of the server with the caller's own credential, or withdraws it. The consent holds for the server as it is configured now; a changed address or authentication needs consent again.")
            .Produces<NhAssistantMcpConnectionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound);
        enabled.MapPost("mcp-connections/complete", CompleteAsync)
            .WithName("NhAssistantCompleteMcpAuthorization")
            .WithSummary("Complete an MCP authorization")
            .WithDescription("Activates the OAuth connection of an authorization the caller started, with the flow and the one-time completion code the callback handed to the caller's browser. An organization connection also needs the admin policy. The code is single use and expires with the flow.")
            .Produces<NhAssistantMcpCompletionDto>()
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status403Forbidden)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> CompleteAsync(HttpContext httpContext, NhAssistantMcpAuthorizationFlows flows)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantMcpCompleteRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.FlowId is not { } flowId)
        {
            return NhAssistantAdminEndpoints.MissingField("flowId");
        }
        if (string.IsNullOrEmpty(body.CompletionCode))
        {
            return NhAssistantAdminEndpoints.MissingField("completionCode");
        }
        var completed = await flows.CompleteAsync(
            flowId,
            body.CompletionCode,
            actor,
            () => NhAssistantEndpointRouteBuilderExtensions.IsAdministratorAsync(httpContext),
            httpContext.RequestAborted);
        if (!completed.Success)
        {
            return NhAssistantAdminEndpoints.Failure(completed);
        }
        var result = completed.Data!;
        return NhAssistantAdminEndpoints.Json(
            new NhAssistantMcpCompletionDto(result.Server.Id, result.OwnerKind, result.Connection.Status ?? NhAssistantMcpConnectionStatuses.Active, result.Connection.GrantedScopes),
            NhAssistantJsonSerializerContext.Default.NhAssistantMcpCompletionDto);
    }

    private static async Task<IResult> GetConnectionsAsync(HttpContext httpContext, NhAssistantMcpConnectionService connections)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var listed = await connections.ListAsync(actor, httpContext.User, httpContext.RequestAborted);
        return NhAssistantAdminEndpoints.Json(
            listed.Select(ToDto).ToArray(),
            NhAssistantJsonSerializerContext.Default.NhAssistantMcpConnectionDtoArray);
    }

    private static async Task<IResult> PutConnectionAsync(
        string serverId,
        HttpContext httpContext,
        NhAssistantMcpConnectionService connections)
    {
        var actor = await NhAssistantAdminEndpoints.ActorAsync(httpContext);
        if (actor is null)
        {
            return NhAssistantAdminEndpoints.ContextUnavailable();
        }
        var body = await NhAssistantAdminEndpoints.ReadAsync(httpContext, NhAssistantJsonSerializerContext.Default.NhAssistantUpdateMcpConnectionRequest);
        if (body is null)
        {
            return NhAssistantAdminEndpoints.Validation();
        }
        if (body.AllowBackgroundUse is not { } allow)
        {
            return NhAssistantAdminEndpoints.MissingField("allowBackgroundUse");
        }
        var updated = await connections.SetBackgroundUseAsync(actor, httpContext.User, serverId, allow, httpContext.RequestAborted);
        return updated.Success
            ? NhAssistantAdminEndpoints.Json(ToDto(updated.Data!), NhAssistantJsonSerializerContext.Default.NhAssistantMcpConnectionDto)
            : NhAssistantAdminEndpoints.Failure(updated);
    }

    private static NhAssistantMcpConnectionDto ToDto(NhAssistantMcpPersonalServer item)
    {
        var host = Uri.TryCreate(item.Server.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        return new NhAssistantMcpConnectionDto(
            item.Server.Id,
            item.Server.DisplayName,
            host,
            item.AllowBackgroundUse,
            item.AllowBackgroundUse ? item.Connection?.ConsentedAt : null,
            item.AgentIds);
    }
}
