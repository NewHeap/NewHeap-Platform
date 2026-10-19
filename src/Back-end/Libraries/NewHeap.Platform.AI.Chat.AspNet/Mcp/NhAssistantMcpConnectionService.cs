using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// One MCP server for which a person decides about background use, with that person's decision.
/// </summary>
internal sealed record NhAssistantMcpPersonalServer(
    AssistantMcpServer Server,
    IReadOnlyList<string> AgentIds,
    AssistantMcpConnection? Connection,
    bool AllowBackgroundUse);

/// <summary>
/// A person's consent to background use of MCP servers that run with that person's credential.
/// Only servers that an administrator allows for personal background use, that are assigned to an
/// agent the person may use and whose required policy the person passes are listed. Consent holds
/// for the server as it is configured now; every change is a content-free audit event.
/// </summary>
internal sealed class NhAssistantMcpConnectionService(
    NhAssistantAdminStore store,
    NhAssistantAgentAccess agentAccess,
    IAuthorizationService authorizationService,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks)
{
    public const string AllowedResult = "allowed";
    public const string WithdrawnResult = "withdrawn";

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
        var listed = (await ListAsync(actorId, user, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Server.Id, serverId, StringComparison.Ordinal));
        if (listed is null)
        {
            return TaskResult<NhAssistantMcpPersonalServer>.Failed(
                NhAssistantAdminErrorCodes.McpServerNotFound,
                "The MCP server was not found.");
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
    /// Servers that run with the credential of the person a headless run acts for.
    /// </summary>
    private static bool IsPersonal(AssistantMcpServer server)
    {
        return server.IsEnabled
            && server.HeadlessUse == NhAssistantMcpHeadlessUses.Personal
            && NhAssistantMcpHeadlessUses.FitsAuthMode(server.HeadlessUse, server.AuthMode);
    }

    private async Task<bool> PassesPolicyAsync(AssistantMcpServer server, ClaimsPrincipal user)
    {
        return server.RequiredPolicy is null
            || (await authorizationService.AuthorizeAsync(user, server.RequiredPolicy)).Succeeded;
    }
}
