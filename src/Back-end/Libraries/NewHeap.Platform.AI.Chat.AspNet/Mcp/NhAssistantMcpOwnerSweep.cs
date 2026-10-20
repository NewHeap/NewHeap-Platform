using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AspNet.Common.Services;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// One batch of the owner sweep: the people it checked, the last of them, where the next batch
/// continues, and the connections it removed.
/// </summary>
internal sealed record NhAssistantMcpOwnerSweepBatch(int Checked, string? LastOwner, int Removed);

/// <summary>
/// Removes the personal MCP connections of people the platform no longer resolves, before
/// <see cref="NhAssistantMcpOAuthOptions.ConnectionRetention"/> would (I16). A person is resolved when
/// <see cref="INhAiPrincipalFactory"/> returns a principal for them with the whole application as
/// subject, the question a background run on the whole application asks: an unknown person, or one
/// who may no longer sign in, is not. Their token connections are deleted, revoked at the
/// authorization server and audited as <see cref="NhAssistantAuditEventKind.McpConnectionDisconnected"/>
/// with <see cref="UnresolvedReason"/>; a consent to background use stays on the row without tokens,
/// as after retention, and grants nothing until the person is resolved and connects again. The
/// built-in factory of a host without the NewHeap user manager resolves nobody, so the sweep does not
/// ask it. A failure for one person is logged and skips that person only.
/// </summary>
internal sealed partial class NhAssistantMcpOwnerSweep(
    NhAssistantMcpOAuthStore store,
    NhAssistantAdminStore servers,
    NhAssistantMcpOAuthConnections connections,
    IServiceProvider services,
    ILogger<NhAssistantMcpOwnerSweep> logger)
{
    /// <summary>The result code of a connection removed because its owner is no longer resolved.</summary>
    public const string UnresolvedReason = "owner-unresolved";

    /// <summary>
    /// Checks the owners of personal token connections after <paramref name="after"/>, at most
    /// <paramref name="take"/>, in the provider's order of actor ids, and removes the connections of
    /// those the platform no longer resolves.
    /// </summary>
    public async Task<NhAssistantMcpOwnerSweepBatch> RunBatchAsync(string? after, int take, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        var principals = ResolvingPrincipalFactory();
        if (principals is null)
        {
            return new NhAssistantMcpOwnerSweepBatch(0, null, 0);
        }

        var owners = await store.ListTokenConnectionOwnersAsync(after ?? string.Empty, take, cancellationToken);
        if (owners.Count == 0)
        {
            return new NhAssistantMcpOwnerSweepBatch(0, null, 0);
        }

        var subject = new NhAiSubjectInfo(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(null, null));
        var removed = 0;
        var unresolved = 0;
        foreach (var owner in owners)
        {
            bool resolved;
            try
            {
                resolved = await principals.CreateAsync(owner, subject, cancellationToken) is not null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                // The next pass asks again; one failing lookup never stops the sweep.
                LogOwnerCheckFailed(logger, exception);
                continue;
            }
            if (resolved)
            {
                continue;
            }

            unresolved++;
            removed += await connections.RemoveOwnerAsync(owner, UnresolvedReason, servers.FindMcpServerAsync, cancellationToken);
        }

        if (removed > 0)
        {
            LogRemoved(logger, unresolved, removed);
        }
        return new NhAssistantMcpOwnerSweepBatch(owners.Count, owners[^1], removed);
    }

    /// <summary>
    /// The principal factory when it can resolve people: not the built-in factory in a host without
    /// the NewHeap user manager, which resolves nobody and would remove every connection.
    /// </summary>
    private INhAiPrincipalFactory? ResolvingPrincipalFactory()
    {
        var principals = services.GetService<INhAiPrincipalFactory>();
        if (principals is null)
        {
            return null;
        }
        var builtIn = principals.GetType().Assembly == typeof(INhAiPrincipalFactory).Assembly;
        return builtIn && services.GetService<INhUserManager>() is null ? null : principals;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The assistant's MCP owner sweep removed {Removed} connections of {People} people the platform no longer resolves.")]
    private static partial void LogRemoved(ILogger logger, int people, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The assistant's MCP owner sweep could not resolve a connection's owner; it asks again at the next pass.")]
    private static partial void LogOwnerCheckFailed(ILogger logger, Exception exception);
}
