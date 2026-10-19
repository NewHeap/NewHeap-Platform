using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Who an OAuth connection belongs to: the organization, or a person (in a tenant; the tenant key is
/// empty in this version).
/// </summary>
internal sealed record NhAssistantMcpConnectionOwner(string Kind, string ActorId, string TenantKey)
{
    public static NhAssistantMcpConnectionOwner Organization { get; } = new(NhAssistantMcpConnectionOwners.Organization, string.Empty, string.Empty);

    public static NhAssistantMcpConnectionOwner User(string actorId)
    {
        return new NhAssistantMcpConnectionOwner(NhAssistantMcpConnectionOwners.User, actorId, string.Empty);
    }
}

/// <summary>
/// Access tokens this process read or renewed, so a request does not unprotect the stored token
/// again; an entry is used only while it is valid beyond the refresh skew.
/// </summary>
internal sealed class NhAssistantMcpTokenCache
{
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset? ExpiresAt)> _entries = new();

    public string? Find(Guid connectionId, DateTimeOffset validUntil)
    {
        return _entries.TryGetValue(connectionId, out var entry) && (entry.ExpiresAt is null || entry.ExpiresAt > validUntil)
            ? entry.Token
            : null;
    }

    public void Set(Guid connectionId, string token, DateTimeOffset? expiresAt)
    {
        _entries[connectionId] = (token, expiresAt);
    }

    public void Remove(Guid connectionId)
    {
        _entries.TryRemove(connectionId, out _);
    }
}

/// <summary>
/// The in-process half of single-flight renewal (D6): one renewal per connection at a time in this
/// process; the lease in the database covers the other instances.
/// </summary>
internal sealed class NhAssistantMcpRenewalGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <summary>This instance's name as lease owner.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public SemaphoreSlim For(Guid connectionId)
    {
        return _gates.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
    }
}

/// <summary>
/// OAuth connections of MCP servers: the access token of an owner, renewed before it expires and
/// after a <c>401</c> with single-flight renewal across instances (D6), client credentials for the
/// organization, step-up after <c>insufficient_scope</c>, the issuer check, and disconnecting with
/// revocation at the authorization server. Tokens never leave as anything but a request header.
/// </summary>
internal sealed partial class NhAssistantMcpOAuthConnections(
    NhAssistantMcpOAuthStore store,
    NhAssistantMcpDiscovery discovery,
    NhAssistantMcpOAuthClient client,
    NhAssistantMcpTokenProtector tokens,
    NhAssistantMcpTokenCache cache,
    NhAssistantMcpRenewalGate gate,
    IOptionsMonitor<NhAssistantOptions> options,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks,
    ILogger<NhAssistantMcpOAuthConnections> logger)
{
    public const string ClientCredentialsConnector = "client-credentials";
    /// <summary>The result code of a disconnect by the retention period.</summary>
    public const string RetentionReason = "retention";
    private static readonly TimeSpan UsedInterval = TimeSpan.FromHours(1);
    private readonly INhAssistantBusinessAuditSink[] _auditSinks = auditSinks.ToArray();

    /// <summary>
    /// The credential of an owner's connection to a server whose authorization server an
    /// administrator confirmed. Client credentials of the organization are obtained on first use.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpCredential>> GetCredentialAsync(
        AssistantMcpServer server,
        NhAssistantMcpConnectionOwner owner,
        CancellationToken cancellationToken)
    {
        if (Unconfirmed(server) is { } unconfirmed)
        {
            return unconfirmed;
        }
        var connection = await store.FindConnectionAsync(server.Id, owner.Kind, owner.ActorId, owner.TenantKey, cancellationToken);
        if (connection?.Status is null)
        {
            if (server.AuthMode == NhAssistantMcpAuthModes.OAuthClientCredentials && owner.Kind == NhAssistantMcpConnectionOwners.Organization)
            {
                return await ConnectWithClientCredentialsAsync(server, cancellationToken);
            }
            return Failed(NhAssistantAdminErrorCodes.McpConnectionRequired);
        }
        if (Unusable(server, connection) is { } unusable)
        {
            return unusable;
        }
        var now = DateTimeOffset.UtcNow;
        var validUntil = now + options.CurrentValue.Mcp.OAuth.RefreshSkew;
        if (cache.Find(connection.Id, validUntil) is { } cached)
        {
            await store.MarkUsedAsync(connection.Id, now, UsedInterval, cancellationToken);
            return Bearer(cached, connection.AccessTokenExpiresAt);
        }
        var access = tokens.UnprotectConnection(server.Id, connection.Id, connection.ProtectedAccessToken);
        if (access is not null && (connection.AccessTokenExpiresAt is null || connection.AccessTokenExpiresAt > validUntil))
        {
            cache.Set(connection.Id, access, connection.AccessTokenExpiresAt);
            await store.MarkUsedAsync(connection.Id, now, UsedInterval, cancellationToken);
            return Bearer(access, connection.AccessTokenExpiresAt);
        }
        return await RenewAsync(server, connection, force: false, scope: null, cancellationToken);
    }

    /// <summary>
    /// Renews after the MCP server refused the token: <c>401</c> renews once (after checking that the
    /// challenge still names the confirmed authorization server), <c>403 insufficient_scope</c> steps
    /// up: client credentials ask for the union of scopes, an authorization code connection records
    /// the scopes for the next authorization.
    /// </summary>
    public async Task<TaskResult<NhAssistantMcpCredential>> RenewAfterChallengeAsync(
        AssistantMcpServer server,
        NhAssistantMcpConnectionOwner owner,
        NhAssistantMcpChallenge challenge,
        CancellationToken cancellationToken)
    {
        if (Unconfirmed(server) is { } unconfirmed)
        {
            return unconfirmed;
        }
        var connection = await store.FindConnectionAsync(server.Id, owner.Kind, owner.ActorId, owner.TenantKey, cancellationToken);
        if (connection?.Status is null)
        {
            return Failed(NhAssistantAdminErrorCodes.McpConnectionRequired);
        }
        if (Unusable(server, connection) is { } unusable)
        {
            return unusable;
        }
        cache.Remove(connection.Id);
        if (challenge.Status == 403)
        {
            return await StepUpAsync(server, connection, challenge, cancellationToken);
        }
        if (challenge.ResourceMetadata is { } metadata && Uri.TryCreate(metadata, UriKind.Absolute, out var metadataUri))
        {
            var named = await discovery.FetchResourceMetadataAsync([metadataUri], server.ResourceUri!, cancellationToken);
            if (named.Success && !named.Data.AuthorizationServers.Contains(server.AuthorizationServerIssuer!, StringComparer.Ordinal))
            {
                // The server names another authorization server: suspend until an administrator confirms.
                await store.SetServerConnectionStatusAsync(server.Id, NhAssistantMcpConnectionStatuses.IssuerChanged, NhAssistantMcpConnectionStatuses.Active, DateTimeOffset.UtcNow, cancellationToken);
                LogIssuerChanged(logger, server.Id);
                return Failed(NhAssistantAdminErrorCodes.McpIssuerChanged);
            }
        }
        return await RenewAsync(server, connection, force: true, scope: null, cancellationToken);
    }

    /// <summary>
    /// Stores the tokens of a completed authorization as the owner's active connection, replacing an
    /// earlier connection of the owner.
    /// </summary>
    public async Task<AssistantMcpConnection> StoreAuthorizationAsync(
        AssistantMcpServer server,
        NhAssistantMcpConnectionOwner owner,
        NhAssistantMcpTokenResponse response,
        string issuer,
        string clientId,
        string resource,
        string? requestedScopes,
        string connectedBy,
        bool? allowBackgroundUse,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = await store.SaveConnectionAsync(
            server.Id,
            owner.Kind,
            owner.ActorId,
            owner.TenantKey,
            row =>
            {
                row.Status = NhAssistantMcpConnectionStatuses.Active;
                row.Issuer = issuer;
                row.ClientId = clientId;
                row.Resource = resource;
                row.GrantedScopes = response.Scope ?? requestedScopes;
                row.RequiredScopes = null;
                row.ProtectedAccessToken = tokens.ProtectConnection(server.Id, row.Id, response.AccessToken);
                row.AccessTokenExpiresAt = response.ExpiresAt;
                row.ProtectedRefreshToken = response.RefreshToken is null ? null : tokens.ProtectConnection(server.Id, row.Id, response.RefreshToken);
                row.RefreshTokenExpiresAt = response.RefreshTokenExpiresAt;
                row.ConnectedBy = connectedBy;
                row.ConnectedAt = now;
                row.LastUsedAt = now;
                row.LastFailureCode = null;
                row.RefreshLeaseUntil = null;
                row.RefreshLeaseOwner = null;
                if (allowBackgroundUse is { } allow)
                {
                    row.AllowBackgroundUse = allow;
                    row.ConsentHash = allow ? NhAssistantMcpConsent.HashOf(server) : null;
                    row.ConsentedAt = allow ? now : null;
                }
            },
            now,
            cancellationToken);
        cache.Set(connection.Id, response.AccessToken, response.ExpiresAt);
        await AuditAsync(NhAssistantAuditEventKind.McpConnectionConnected, server.Id, connectedBy, owner.Kind == NhAssistantMcpConnectionOwners.Organization ? NhAssistantMcpConnectionOwners.Organization : NhAssistantMcpConnectionOwners.User, cancellationToken);
        return connection;
    }

    /// <summary>
    /// Removes token connections of a server, of one owner or all, then revokes their tokens at the
    /// authorization server, best effort after the local delete.
    /// </summary>
    public async Task<int> DisconnectAsync(
        AssistantMcpServer server,
        string? ownerKind,
        string? ownerActorId,
        string reason,
        string actorId,
        CancellationToken cancellationToken)
    {
        var removed = await store.DeleteTokenConnectionsAsync(server.Id, ownerKind, ownerActorId, cancellationToken);
        foreach (var connection in removed)
        {
            cache.Remove(connection.Id);
            await RevokeAsync(server, connection, cancellationToken);
            await AuditAsync(NhAssistantAuditEventKind.McpConnectionDisconnected, server.Id, string.IsNullOrEmpty(connection.OwnerActorId) ? actorId : connection.OwnerActorId, reason, cancellationToken);
        }
        return removed.Count;
    }

    /// <summary>
    /// Removes personal connections unused for the retention period and revokes their tokens; the
    /// person connects again when an agent needs the server.
    /// </summary>
    public async Task<int> RemoveStaleAsync(
        Func<string, CancellationToken, Task<AssistantMcpServer?>> findServer,
        int take,
        CancellationToken cancellationToken)
    {
        var before = DateTimeOffset.UtcNow - options.CurrentValue.Mcp.OAuth.ConnectionRetention;
        var removed = 0;
        foreach (var stale in await store.ListStaleUserConnectionsAsync(before, take, cancellationToken))
        {
            if (await store.DeleteIfStaleAsync(stale.Id, before, cancellationToken) is not { } connection)
            {
                continue;
            }
            cache.Remove(connection.Id);
            if (await findServer(connection.ServerId, cancellationToken) is { } server)
            {
                await RevokeAsync(server, connection, cancellationToken);
            }
            await AuditAsync(NhAssistantAuditEventKind.McpConnectionDisconnected, connection.ServerId, connection.OwnerActorId, RetentionReason, cancellationToken);
            removed++;
        }
        return removed;
    }

    private async Task RevokeAsync(AssistantMcpServer server, AssistantMcpConnection connection, CancellationToken cancellationToken)
    {
        var issuer = connection.Issuer ?? server.AuthorizationServerIssuer;
        if (issuer is null)
        {
            return;
        }
        try
        {
            var metadata = await discovery.GetAuthorizationServerAsync(issuer, bypassCache: false, cancellationToken);
            if (!metadata.Success || metadata.Data!.RevocationEndpoint is null)
            {
                return;
            }
            var identity = await client.ResolveClientAsync(server, metadata.Data, cancellationToken);
            if (!identity.Success)
            {
                return;
            }
            if (tokens.UnprotectConnection(server.Id, connection.Id, connection.ProtectedRefreshToken) is { } refresh)
            {
                await client.RevokeAsync(identity.Data!, metadata.Data, refresh, "refresh_token", cancellationToken);
            }
            if (tokens.UnprotectConnection(server.Id, connection.Id, connection.ProtectedAccessToken) is { } access)
            {
                await client.RevokeAsync(identity.Data!, metadata.Data, access, "access_token", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogRevocationFailed(logger, server.Id, exception.GetType().Name);
        }
    }

    private async Task<TaskResult<NhAssistantMcpCredential>> ConnectWithClientCredentialsAsync(
        AssistantMcpServer server,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = await store.SaveConnectionAsync(
            server.Id,
            NhAssistantMcpConnectionOwners.Organization,
            string.Empty,
            string.Empty,
            row =>
            {
                // A row without tokens yet: the renewal below obtains the first one under the lease.
                row.Status ??= NhAssistantMcpConnectionStatuses.Active;
                row.Issuer ??= server.AuthorizationServerIssuer;
                row.Resource ??= server.ResourceUri;
                row.ClientId ??= server.OAuthClientId;
                row.ConnectedBy ??= ClientCredentialsConnector;
                row.ConnectedAt ??= now;
            },
            now,
            cancellationToken);
        var renewed = await RenewAsync(server, connection, force: true, scope: null, cancellationToken);
        if (renewed.Success && connection.ProtectedAccessToken is null)
        {
            await AuditAsync(NhAssistantAuditEventKind.McpConnectionConnected, server.Id, ClientCredentialsConnector, NhAssistantMcpConnectionOwners.Organization, cancellationToken);
        }
        return renewed;
    }

    private async Task<TaskResult<NhAssistantMcpCredential>> StepUpAsync(
        AssistantMcpServer server,
        AssistantMcpConnection connection,
        NhAssistantMcpChallenge challenge,
        CancellationToken cancellationToken)
    {
        var granted = NhAssistantMcpScopes.Split(connection.GrantedScopes);
        var wanted = granted.Union(challenge.Scopes, StringComparer.Ordinal).ToArray();
        var allowed = NhAssistantMcpScopes.Split(server.OAuthScopes);
        if (allowed.Count > 0 && wanted.Any(scope => !allowed.Contains(scope, StringComparer.Ordinal)))
        {
            return Failed(NhAssistantAdminErrorCodes.McpScopeNotAllowed);
        }
        if (challenge.Scopes.Count == 0 || wanted.Length == granted.Count)
        {
            return Failed(NhAssistantAdminErrorCodes.McpInsufficientScope);
        }
        if (server.AuthMode == NhAssistantMcpAuthModes.OAuthClientCredentials)
        {
            return await RenewAsync(server, connection, force: true, scope: NhAssistantMcpScopes.Join(wanted), cancellationToken);
        }
        // An authorization code connection cannot widen itself: its owner authorizes again.
        await store.RecordRequiredScopesAsync(connection.Id, NhAssistantMcpScopes.Join(wanted)!, DateTimeOffset.UtcNow, cancellationToken);
        return Failed(NhAssistantAdminErrorCodes.McpInsufficientScope);
    }

    private async Task<TaskResult<NhAssistantMcpCredential>> RenewAsync(
        AssistantMcpServer server,
        AssistantMcpConnection connection,
        bool force,
        string? scope,
        CancellationToken cancellationToken)
    {
        var oauth = options.CurrentValue.Mcp.OAuth;
        var semaphore = gate.For(connection.Id);
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            // Another request of this process may have renewed while this one waited.
            var current = await store.FindConnectionAsync(connection.Id, cancellationToken);
            if (current?.Status is null)
            {
                return Failed(NhAssistantAdminErrorCodes.McpConnectionRequired);
            }
            if (Unusable(server, current) is { } unusable)
            {
                return unusable;
            }
            if (Valid(server, current, oauth) is { } fresh && (!force || current.Version != connection.Version) && scope is null)
            {
                return fresh;
            }

            var now = DateTimeOffset.UtcNow;
            if (!await store.TryTakeRefreshLeaseAsync(current.Id, gate.InstanceId, now, now + oauth.RefreshLeaseDuration, cancellationToken))
            {
                return await AwaitOtherRenewalAsync(server, current, oauth, cancellationToken);
            }

            var metadata = await discovery.GetAuthorizationServerAsync(server.AuthorizationServerIssuer!, bypassCache: false, cancellationToken);
            if (!metadata.Success)
            {
                await store.ReleaseLeaseAsync(current.Id, gate.InstanceId, NhAssistantAdminErrorCodes.McpTokenRequestFailed, now, cancellationToken);
                return Failed(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
            }
            var identity = await client.ResolveClientAsync(server, metadata.Data!, cancellationToken);
            if (!identity.Success)
            {
                var code = FirstCode(identity) ?? NhAssistantAdminErrorCodes.McpTokenRequestFailed;
                await store.ReleaseLeaseAsync(current.Id, gate.InstanceId, code, now, cancellationToken);
                return Failed(code);
            }

            TaskResult<NhAssistantMcpTokenResponse> response;
            if (server.AuthMode == NhAssistantMcpAuthModes.OAuthClientCredentials)
            {
                response = await client.ClientCredentialsAsync(
                    identity.Data!,
                    metadata.Data!,
                    server.ResourceUri!,
                    scope ?? current.GrantedScopes ?? server.OAuthScopes,
                    cancellationToken);
            }
            else
            {
                var refresh = tokens.UnprotectConnection(server.Id, current.Id, current.ProtectedRefreshToken);
                if (refresh is null)
                {
                    return await EndAsync(server, current, NhAssistantAdminErrorCodes.McpReauthorizationRequired, cancellationToken);
                }
                response = await client.RefreshAsync(identity.Data!, metadata.Data!, refresh, current.Resource ?? server.ResourceUri!, cancellationToken);
            }
            if (!response.Success)
            {
                var code = FirstCode(response) ?? NhAssistantAdminErrorCodes.McpTokenRequestFailed;
                if (code == NhAssistantAdminErrorCodes.McpReauthorizationRequired && server.AuthMode == NhAssistantMcpAuthModes.OAuth)
                {
                    return await EndAsync(server, current, code, cancellationToken);
                }
                // Network errors and 5xx keep the connection; only this call fails.
                await store.ReleaseLeaseAsync(current.Id, gate.InstanceId, code, now, cancellationToken);
                return Failed(code == NhAssistantAdminErrorCodes.McpReauthorizationRequired ? NhAssistantAdminErrorCodes.McpTokenRequestFailed : code);
            }

            var token = response.Data!;
            var stored = await store.StoreRenewedTokensAsync(
                current.Id,
                gate.InstanceId,
                tokens.ProtectConnection(server.Id, current.Id, token.AccessToken),
                token.ExpiresAt,
                token.RefreshToken is null ? null : tokens.ProtectConnection(server.Id, current.Id, token.RefreshToken),
                token.RefreshTokenExpiresAt,
                token.Scope,
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (!stored)
            {
                // The lease ran out and another instance took over; its tokens win.
                return await AwaitOtherRenewalAsync(server, current, oauth, cancellationToken);
            }
            cache.Set(current.Id, token.AccessToken, token.ExpiresAt);
            return Bearer(token.AccessToken, token.ExpiresAt);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>Waits up to the lease duration for another instance's renewal, then reads the row again.</summary>
    private async Task<TaskResult<NhAssistantMcpCredential>> AwaitOtherRenewalAsync(
        AssistantMcpServer server,
        AssistantMcpConnection current,
        NhAssistantMcpOAuthOptions oauth,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + oauth.RefreshLeaseDuration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var again = await store.FindConnectionAsync(current.Id, cancellationToken);
            if (again?.Status is null)
            {
                return Failed(NhAssistantAdminErrorCodes.McpConnectionRequired);
            }
            if (again.RefreshLeaseOwner is not null)
            {
                continue;
            }
            if (Unusable(server, again) is { } unusable)
            {
                return unusable;
            }
            return Valid(server, again, oauth) ?? Failed(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
        }
        return Failed(NhAssistantAdminErrorCodes.McpTokenRequestFailed);
    }

    private TaskResult<NhAssistantMcpCredential>? Valid(AssistantMcpServer server, AssistantMcpConnection connection, NhAssistantMcpOAuthOptions oauth)
    {
        var access = tokens.UnprotectConnection(server.Id, connection.Id, connection.ProtectedAccessToken);
        if (access is null || (connection.AccessTokenExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow + oauth.RefreshSkew))
        {
            return null;
        }
        cache.Set(connection.Id, access, connection.AccessTokenExpiresAt);
        return Bearer(access, connection.AccessTokenExpiresAt);
    }

    private async Task<TaskResult<NhAssistantMcpCredential>> EndAsync(
        AssistantMcpServer server,
        AssistantMcpConnection connection,
        string code,
        CancellationToken cancellationToken)
    {
        cache.Remove(connection.Id);
        await store.MarkReauthorizationRequiredAsync(connection.Id, code, DateTimeOffset.UtcNow, cancellationToken);
        await AuditAsync(
            NhAssistantAuditEventKind.McpConnectionReauthorizationRequired,
            server.Id,
            string.IsNullOrEmpty(connection.OwnerActorId) ? connection.ConnectedBy ?? string.Empty : connection.OwnerActorId,
            code,
            cancellationToken);
        return Failed(NhAssistantAdminErrorCodes.McpReauthorizationRequired);
    }

    private static TaskResult<NhAssistantMcpCredential>? Unconfirmed(AssistantMcpServer server)
    {
        return server.AuthorizationServerIssuer is null || server.ResourceUri is null || server.AuthorizationConfirmedAt is null
            ? Failed(NhAssistantAdminErrorCodes.McpAuthorizationNotConfirmed)
            : null;
    }

    private static TaskResult<NhAssistantMcpCredential>? Unusable(AssistantMcpServer server, AssistantMcpConnection connection)
    {
        if (connection.Status == NhAssistantMcpConnectionStatuses.IssuerChanged
            || (connection.Issuer is not null && !string.Equals(connection.Issuer, server.AuthorizationServerIssuer, StringComparison.Ordinal)))
        {
            return Failed(NhAssistantAdminErrorCodes.McpIssuerChanged);
        }
        return connection.Status == NhAssistantMcpConnectionStatuses.ReauthorizationRequired
            ? Failed(NhAssistantAdminErrorCodes.McpReauthorizationRequired)
            : null;
    }

    private async Task AuditAsync(NhAssistantAuditEventKind kind, string serverId, string actorId, string resultCode, CancellationToken cancellationToken)
    {
        var evt = new NhAssistantAuditEvent(kind, Guid.Empty, Guid.Empty, actorId, string.Empty, 0, DateTimeOffset.UtcNow)
        {
            ObjectId = serverId,
            ResultCode = resultCode
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private static TaskResult<NhAssistantMcpCredential> Bearer(string token, DateTimeOffset? expiresAt)
    {
        return TaskResult<NhAssistantMcpCredential>.Succeeded(new NhAssistantMcpCredential(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer " + token },
            expiresAt));
    }

    private static TaskResult<NhAssistantMcpCredential> Failed(string code)
    {
        return NhAssistantMcpCredentialResults.Failed(code);
    }

    private static string? FirstCode(TaskResult result)
    {
        return NhAssistantMcpCredentialResults.FirstCode(result);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server {ServerId} names another authorization server than the confirmed one; its connections are suspended.")]
    private static partial void LogIssuerChanged(ILogger logger, string serverId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Revoking the tokens of a connection of MCP server {ServerId} failed ({ExceptionType}).")]
    private static partial void LogRevocationFailed(ILogger logger, string serverId, string exceptionType);
}
