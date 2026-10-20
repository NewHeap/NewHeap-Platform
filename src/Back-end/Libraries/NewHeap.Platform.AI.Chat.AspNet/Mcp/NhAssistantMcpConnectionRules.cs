using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The one rule of which OAuth connections a server has, for every connection endpoint of people and
/// administrators. Authorizing needs the OAuth authorization code flow (<c>oauth</c>); the
/// organization's connection exists for <c>oauth</c> and <c>oauth-client-credentials</c>; personal
/// connections exist only for an <c>oauth</c> server that each person connects to, where an
/// administrator's connection endpoints act on the administrator's own connection. Any other request
/// fails with <see cref="NhAssistantAdminErrorCodes.McpConnectionNotSupported"/>.
/// </summary>
internal static class NhAssistantMcpConnectionRules
{
    /// <summary>
    /// Whose connection an administrator authorizes: the organization's, or the administrator's own
    /// for a server each person connects to.
    /// </summary>
    public static TaskResult<NhAssistantMcpConnectionOwner> AuthorizedConnection(AssistantMcpServer server, string actorId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (server.AuthMode != NhAssistantMcpAuthModes.OAuth)
        {
            return NotSupported<NhAssistantMcpConnectionOwner>();
        }
        return TaskResult<NhAssistantMcpConnectionOwner>.Succeeded(OwnerFor(server, actorId));
    }

    /// <summary>
    /// The connection an administrator reads or removes: the organization's of an <c>oauth</c> or
    /// <c>oauth-client-credentials</c> server, the administrator's own for a server each person
    /// connects to.
    /// </summary>
    public static TaskResult<NhAssistantMcpConnectionOwner> AdministeredConnection(AssistantMcpServer server, string actorId)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (!NhAssistantMcpAuthModes.IsOAuth(server.AuthMode))
        {
            return NotSupported<NhAssistantMcpConnectionOwner>();
        }
        return TaskResult<NhAssistantMcpConnectionOwner>.Succeeded(OwnerFor(server, actorId));
    }

    /// <summary>
    /// Whether the server has personal connections: an <c>oauth</c> server each person connects to.
    /// </summary>
    public static TaskResult PersonalConnections(AssistantMcpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return NhAssistantMcpToolSource.IsPersonalOAuth(server)
            ? TaskResult.Succeeded()
            : NotSupported<bool>();
    }

    private static NhAssistantMcpConnectionOwner OwnerFor(AssistantMcpServer server, string actorId)
    {
        return NhAssistantMcpToolSource.IsPersonalOAuth(server)
            ? NhAssistantMcpConnectionOwner.User(actorId)
            : NhAssistantMcpConnectionOwner.Organization;
    }

    private static TaskResult<T> NotSupported<T>()
    {
        return TaskResult<T>.Failed(
            NhAssistantAdminErrorCodes.McpConnectionNotSupported,
            "The MCP server has no OAuth connection of this kind.");
    }
}
