using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// One MCP server that runs with a person's own account, with that person's connection and decision
/// about background use.
/// </summary>
internal sealed record NhAssistantMcpPersonalServer(
    AssistantMcpServer Server,
    IReadOnlyList<string> AgentIds,
    AssistantMcpConnection? Connection,
    bool AllowBackgroundUse)
{
    /// <summary>Whether the person connects the server with OAuth.</summary>
    public bool IsOAuth => NhAssistantMcpToolSource.IsPersonalOAuth(Server);

    /// <summary>Whether an administrator offers the server in background runs with the person's account.</summary>
    public bool OffersBackgroundUse => Server.HeadlessUse == NhAssistantMcpHeadlessUses.Personal;

    /// <summary>
    /// For an OAuth server: <c>not-connected</c> or the connection's status
    /// (<see cref="NhAssistantMcpConnectionStatuses"/>); null otherwise.
    /// </summary>
    public string? ConnectionStatus => IsOAuth ? Connection?.Status ?? NotConnected : null;

    public const string NotConnected = "not-connected";
}

/// <summary>
/// A person's own MCP connections: servers each person connects to with OAuth, and servers an
/// administrator allows in background runs with the person's account, with the person's consent.
/// Only servers that are assigned to an agent the person may use and whose required policy the
/// person passes are listed. Consent holds for the server as it is configured now; every change is a
/// content-free audit event.
/// </summary>
internal sealed class NhAssistantMcpConnectionService(
    NhAssistantAdminStore store,
    NhAssistantAgentAccess agentAccess,
    IAuthorizationService authorizationService,
    NhAssistantMcpOAuthConnections oauthConnections,
    NhAssistantMcpAuthorizationFlows flows,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks)
{
    public const string AllowedResult = "allowed";
    public const string WithdrawnResult = "withdrawn";

    /// <summary>The result code of a disconnect by the person.</summary>
    public const string PersonReason = "person";

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public async Task<IReadOnlyList<NhAssistantMcpPersonalServer>> ListAsync(
        string actorId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var visibleAgents = (await agentAccess.GetVisibleAgentsAsync(user, cancellationToken))
            .Select(agent => agent.Id)
            .ToHashSet(StringComparer.Ordinal);
        var connections = (await store.ListMcpConnectionsAsync(
                NhAssistantMcpConnectionOwners.User,
                actorId,
                string.Empty,
                cancellationToken))
            .ToDictionary(connection => connection.ServerId, StringComparer.Ordinal);

        var result = new List<NhAssistantMcpPersonalServer>();
        foreach (var (server, agentIds) in await store.ListMcpServersAsync(cancellationToken))
        {
            var usableAgents = agentIds.Where(visibleAgents.Contains).ToArray();
            if (!IsPersonal(server) || usableAgents.Length == 0 || !await PassesPolicyAsync(server, user))
            {
                continue;
            }
            connections.TryGetValue(server.Id, out var connection);
            result.Add(new NhAssistantMcpPersonalServer(server, usableAgents, connection, NhAssistantMcpConsent.Allows(connection, server)));
        }
        return result;
    }

    public async Task<TaskResult<NhAssistantMcpPersonalServer>> SetBackgroundUseAsync(
        string actorId,
        ClaimsPrincipal user,
        string serverId,
        bool allow,
        CancellationToken cancellationToken)
    {
        var listed = await FindAsync(actorId, user, serverId, cancellationToken);
        if (listed is null)
        {
            return NotFound();
        }
        if (!listed.OffersBackgroundUse)
        {
            return TaskResult<NhAssistantMcpPersonalServer>.Failed(
                NhAssistantAdminErrorCodes.McpHeadlessNotAllowed,
                "The MCP server is not offered in background runs.");
        }

        var connection = await store.SetMcpBackgroundUseAsync(
            listed.Server.Id,
            actorId,
            string.Empty,
            allow,
            NhAssistantMcpConsent.HashOf(listed.Server),
            DateTimeOffset.UtcNow,
            cancellationToken);
        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.McpBackgroundUseChanged,
            Guid.Empty,
            Guid.Empty,
            actorId,
            string.Empty,
            0,
            DateTimeOffset.UtcNow)
        {
            ObjectId = listed.Server.Id,
            ResultCode = allow ? AllowedResult : WithdrawnResult
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
        return TaskResult<NhAssistantMcpPersonalServer>.Succeeded(
            listed with { Connection = connection, AllowBackgroundUse = NhAssistantMcpConsent.Allows(connection, listed.Server) });
    }

    /// <summary>
    /// Starts the person's authorization of a server each person connects to with OAuth. With
    /// <paramref name="allowBackgroundUse"/> the person also consents to background use, when an
    /// administrator offers it.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpAuthorizationStart>> AuthorizeAsync(
        string actorId,
        ClaimsPrincipal user,
        string serverId,
        string? returnUrl,
        bool allowBackgroundUse,
        CancellationToken cancellationToken)
    {
        var listed = await FindAsync(actorId, user, serverId, cancellationToken);
        if (listed is null)
        {
            return TaskResult<NhAssistantMcpAuthorizationStart>.Failed(NhAssistantAdminErrorCodes.McpServerNotFound, "The MCP server was not found.");
        }
        var personal = NhAssistantMcpConnectionRules.PersonalConnections(listed.Server);
        if (!personal.Success)
        {
            return TaskResult<NhAssistantMcpAuthorizationStart>.Failed(personal);
        }
        return await flows.StartAsync(
            listed.Server,
            NhAssistantMcpConnectionOwner.User(actorId),
            actorId,
            allowBackgroundUse && listed.OffersBackgroundUse,
            returnUrl,
            cancellationToken);
    }

    /// <summary>
    /// Removes the person's OAuth connection of a server and revokes its tokens. A consent to
    /// background use stays until the person withdraws it (Q10: an approved proposal then fails as
    /// not connected).
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpPersonalServer>> DisconnectAsync(
        string actorId,
        ClaimsPrincipal user,
        string serverId,
        CancellationToken cancellationToken)
    {
        var listed = await FindAsync(actorId, user, serverId, cancellationToken);
        if (listed is null)
        {
            return NotFound();
        }
        var personal = NhAssistantMcpConnectionRules.PersonalConnections(listed.Server);
        if (!personal.Success)
        {
            return TaskResult<NhAssistantMcpPersonalServer>.Failed(personal);
        }
        await oauthConnections.DisconnectAsync(
            listed.Server,
            NhAssistantMcpConnectionOwners.User,
            actorId,
            PersonReason,
            actorId,
            cancellationToken);
        // The consent to background use stays until the person withdraws it.
        var after = await FindAsync(actorId, user, serverId, cancellationToken);
        return after is null ? NotFound() : TaskResult<NhAssistantMcpPersonalServer>.Succeeded(after);
    }

    private async Task<NhAssistantMcpPersonalServer?> FindAsync(
        string actorId,
        ClaimsPrincipal user,
        string serverId,
        CancellationToken cancellationToken)
    {
        return (await ListAsync(actorId, user, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Server.Id, serverId, StringComparison.Ordinal));
    }

    private static TaskResult<NhAssistantMcpPersonalServer> NotFound()
    {
        return TaskResult<NhAssistantMcpPersonalServer>.Failed(
            NhAssistantAdminErrorCodes.McpServerNotFound,
            "The MCP server was not found.");
    }

    /// <summary>
    /// Servers that run with a person's own account: each person connects to them with OAuth, or an
    /// administrator offers them in background runs with the person's forwarded sign-in.
    /// </summary>
    private static bool IsPersonal(AssistantMcpServer server)
    {
        if (!server.IsEnabled)
        {
            return false;
        }
        return NhAssistantMcpToolSource.IsPersonalOAuth(server)
            || (server.HeadlessUse == NhAssistantMcpHeadlessUses.Personal
                && NhAssistantMcpHeadlessUses.FitsAuthMode(server.HeadlessUse, server.AuthMode, server.ConnectionScope));
    }

    private async Task<bool> PassesPolicyAsync(AssistantMcpServer server, ClaimsPrincipal user)
    {
        return server.RequiredPolicy is null
            || (await authorizationService.AuthorizeAsync(user, server.RequiredPolicy)).Succeeded;
    }
}
