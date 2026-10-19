using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The credential of <c>oauth</c> and <c>oauth-client-credentials</c> servers: the access token of the
/// organization's connection, or of the person a request or headless run is for when each person
/// connects their own account. A singleton, because cached organization connections keep it; each
/// call works in a service scope of its own.
/// </summary>
internal sealed class NhAssistantMcpOAuthCredentialProvider(IServiceScopeFactory scopes) : INhAssistantMcpCredentialProvider
{
    public bool CanRenew => true;

    public bool Supports(string authMode)
    {
        return NhAssistantMcpAuthModes.IsOAuth(authMode);
    }

    /// <summary>
    /// Whose connection a request uses: the organization's, or the person's that the subject names.
    /// </summary>
    public static NhAssistantMcpConnectionOwner? OwnerOf(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject)
    {
        if (NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode, server.ConnectionScope))
        {
            return NhAssistantMcpConnectionOwner.Organization;
        }
        return string.IsNullOrEmpty(subject.PersonActorId) ? null : NhAssistantMcpConnectionOwner.User(subject.PersonActorId);
    }

    public async ValueTask<TaskResult<NhAssistantMcpCredential>> GetAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        if (OwnerOf(server, subject) is not { } owner)
        {
            return NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpConnectionRequired);
        }
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpOAuthConnections>()
            .GetCredentialAsync(server, owner, cancellationToken);
    }

    public async ValueTask<TaskResult<NhAssistantMcpCredential>> RenewAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        NhAssistantMcpChallenge challenge,
        CancellationToken cancellationToken)
    {
        if (OwnerOf(server, subject) is not { } owner)
        {
            return NhAssistantMcpCredentialResults.Failed(NhAssistantAdminErrorCodes.McpConnectionRequired);
        }
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpOAuthConnections>()
            .RenewAfterChallengeAsync(server, owner, challenge, cancellationToken);
    }

    public TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
    {
        if (NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode, server.ConnectionScope))
        {
            return TaskResult.Succeeded();
        }
        // A person's connection: in a request only that person's rights, in a headless run exactly the
        // rights the person lends (Q3, D10).
        var allowed = subject.IsHeadless
            ? call.Invocation.GrantSource == subject.PersonGrantSource
            : call.Invocation.GrantSource is NhAiGrantSource.None or NhAiGrantSource.Delegated;
        return allowed
            ? TaskResult.Succeeded()
            : TaskResult.Failed(
                NhAssistantAdminErrorCodes.McpCredentialMismatch,
                "The call was not authorized for the person whose connection the request carries.");
    }
}
