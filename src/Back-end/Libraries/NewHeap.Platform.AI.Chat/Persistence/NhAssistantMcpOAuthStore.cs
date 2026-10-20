using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of OAuth connections, authorization flows and client registrations of MCP servers.
/// Tokens arrive and leave protected; this store never sees them in plain text. Renewal is guarded by
/// a lease taken with a conditional update, so one instance renews a connection at a time.
/// </summary>
internal sealed class NhAssistantMcpOAuthStore(NhAssistantDbContextFactory contextFactory)
{
    public const string FlowPending = "pending";
    public const string FlowExchanged = "exchanged";
    public const string FlowCompleted = "completed";
    public const string FlowFailed = "failed";

    // ----- Connections -----

    public async Task<AssistantMcpConnection?> FindConnectionAsync(
        string serverId,
        string ownerKind,
        string ownerActorId,
        string tenantKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(
                connection => connection.ServerId == serverId
                    && connection.OwnerKind == ownerKind
                    && connection.OwnerActorId == ownerActorId
                    && connection.TenantKey == tenantKey,
                cancellationToken);
    }

    public async Task<AssistantMcpConnection?> FindConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections.AsNoTracking().SingleOrDefaultAsync(connection => connection.Id == id, cancellationToken);
    }

    /// <summary>
    /// Creates or updates the connection row of an owner and applies <paramref name="apply"/> to it.
    /// A concurrent first write of the same owner wins once and the other attempt updates its row.
    /// </summary>
    public async Task<AssistantMcpConnection> SaveConnectionAsync(
        string serverId,
        string ownerKind,
        string ownerActorId,
        string tenantKey,
        Action<AssistantMcpConnection> apply,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var context = contextFactory.CreateDbContext();
            var connection = await context.McpConnections.SingleOrDefaultAsync(
                item => item.ServerId == serverId
                    && item.OwnerKind == ownerKind
                    && item.OwnerActorId == ownerActorId
                    && item.TenantKey == tenantKey,
                cancellationToken);
            if (connection is null)
            {
                connection = new AssistantMcpConnection
                {
                    Id = Guid.NewGuid(),
                    ServerId = serverId,
                    OwnerKind = ownerKind,
                    OwnerActorId = ownerActorId,
                    TenantKey = tenantKey,
                    CreatedAt = now
                };
                context.McpConnections.Add(connection);
            }
            apply(connection);
            connection.UpdatedAt = now;
            connection.Version++;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return connection;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A concurrent write created the row or changed its version: apply on top of it.
            }
        }
    }

    /// <summary>
    /// Takes the renewal lease of a connection when nobody holds it or it ran out.
    /// </summary>
    public async Task<bool> TryTakeRefreshLeaseAsync(
        Guid id,
        string owner,
        DateTimeOffset now,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var taken = await context.McpConnections
            .Where(connection => connection.Id == id
                && (connection.RefreshLeaseUntil == null || connection.RefreshLeaseUntil < now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.RefreshLeaseUntil, until)
                    .SetProperty(connection => connection.RefreshLeaseOwner, owner)
                    .SetProperty(connection => connection.Version, connection => connection.Version + 1),
                cancellationToken);
        return taken == 1;
    }

    /// <summary>
    /// Stores renewed tokens and releases the lease, only while <paramref name="owner"/> holds it.
    /// A null <paramref name="protectedRefreshToken"/> keeps the stored refresh token.
    /// </summary>
    public async Task<bool> StoreRenewedTokensAsync(
        Guid id,
        string owner,
        string protectedAccessToken,
        DateTimeOffset? accessTokenExpiresAt,
        string? protectedRefreshToken,
        DateTimeOffset? refreshTokenExpiresAt,
        string? grantedScopes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var stored = await context.McpConnections
            .Where(connection => connection.Id == id && connection.RefreshLeaseOwner == owner)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.ProtectedAccessToken, protectedAccessToken)
                    .SetProperty(connection => connection.AccessTokenExpiresAt, accessTokenExpiresAt)
                    .SetProperty(connection => connection.ProtectedRefreshToken, connection => protectedRefreshToken ?? connection.ProtectedRefreshToken)
                    .SetProperty(connection => connection.RefreshTokenExpiresAt, connection => protectedRefreshToken == null ? connection.RefreshTokenExpiresAt : refreshTokenExpiresAt)
                    .SetProperty(connection => connection.GrantedScopes, connection => grantedScopes ?? connection.GrantedScopes)
                    .SetProperty(connection => connection.Status, NhAssistantMcpConnectionStatuses.Active)
                    .SetProperty(connection => connection.LastFailureCode, (string?)null)
                    .SetProperty(connection => connection.LastRefreshedAt, now)
                    .SetProperty(connection => connection.RefreshLeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(connection => connection.RefreshLeaseOwner, (string?)null)
                    .SetProperty(connection => connection.UpdatedAt, now)
                    .SetProperty(connection => connection.Version, connection => connection.Version + 1),
                cancellationToken);
        return stored == 1;
    }

    /// <summary>
    /// Ends a connection whose tokens can no longer be renewed: the tokens are removed and the owner
    /// has to connect again.
    /// </summary>
    public async Task MarkReauthorizationRequiredAsync(
        Guid id,
        string failureCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.McpConnections
            .Where(connection => connection.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.Status, NhAssistantMcpConnectionStatuses.ReauthorizationRequired)
                    .SetProperty(connection => connection.ProtectedAccessToken, (string?)null)
                    .SetProperty(connection => connection.ProtectedRefreshToken, (string?)null)
                    .SetProperty(connection => connection.AccessTokenExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(connection => connection.RefreshTokenExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(connection => connection.LastFailureCode, failureCode)
                    .SetProperty(connection => connection.RefreshLeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(connection => connection.RefreshLeaseOwner, (string?)null)
                    .SetProperty(connection => connection.UpdatedAt, now)
                    .SetProperty(connection => connection.Version, connection => connection.Version + 1),
                cancellationToken);
    }

    /// <summary>Records a renewal failure that leaves the connection usable, and releases the lease.</summary>
    public async Task ReleaseLeaseAsync(Guid id, string owner, string? failureCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.McpConnections
            .Where(connection => connection.Id == id && connection.RefreshLeaseOwner == owner)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.LastFailureCode, failureCode)
                    .SetProperty(connection => connection.RefreshLeaseUntil, (DateTimeOffset?)null)
                    .SetProperty(connection => connection.RefreshLeaseOwner, (string?)null)
                    .SetProperty(connection => connection.UpdatedAt, now)
                    .SetProperty(connection => connection.Version, connection => connection.Version + 1),
                cancellationToken);
    }

    /// <summary>Records the scopes a server asked for that the connection lacks.</summary>
    public async Task RecordRequiredScopesAsync(Guid id, string requiredScopes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.McpConnections
            .Where(connection => connection.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.RequiredScopes, requiredScopes)
                    .SetProperty(connection => connection.LastFailureCode, NhAssistantAdminErrorCodes.McpInsufficientScope)
                    .SetProperty(connection => connection.UpdatedAt, now)
                    .SetProperty(connection => connection.Version, connection => connection.Version + 1),
                cancellationToken);
    }

    /// <summary>Marks when a connection was last used, at most once per <paramref name="interval"/>.</summary>
    public async Task MarkUsedAsync(Guid id, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken)
    {
        var before = now - interval;
        await using var context = contextFactory.CreateDbContext();
        await context.McpConnections
            .Where(connection => connection.Id == id && (connection.LastUsedAt == null || connection.LastUsedAt < before))
            .ExecuteUpdateAsync(setters => setters.SetProperty(connection => connection.LastUsedAt, now), cancellationToken);
    }

    /// <summary>Sets the status of every connection of a server that holds tokens.</summary>
    public async Task<int> SetServerConnectionStatusAsync(
        string serverId,
        string status,
        string? onlyFromStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .Where(connection => connection.ServerId == serverId
                && connection.Status != null
                && (onlyFromStatus == null || connection.Status == onlyFromStatus))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(connection => connection.Status, status)
                    .SetProperty(connection => connection.UpdatedAt, now)
                    .SetProperty(connection => connection.Version, connection => connection.Version + 1),
                cancellationToken);
    }

    /// <summary>
    /// Removes the token connections of a server, optionally of one owner kind or one owner, and
    /// returns them as they were for revocation at the authorization server. A person's consent to
    /// background use stays: the row keeps it without tokens, so an approved proposal of that person
    /// fails as not connected. Rows that only record consent stay as they are.
    /// </summary>
    public async Task<IReadOnlyList<AssistantMcpConnection>> DeleteTokenConnectionsAsync(
        string serverId,
        string? ownerKind,
        string? ownerActorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var connections = await context.McpConnections
            .AsNoTracking()
            .Where(connection => connection.ServerId == serverId
                && connection.Status != null
                && (ownerKind == null || connection.OwnerKind == ownerKind)
                && (ownerActorId == null || connection.OwnerActorId == ownerActorId))
            .ToListAsync(cancellationToken);
        if (connections.Count == 0)
        {
            return [];
        }
        var keep = connections
            .Where(connection => connection.OwnerKind == NhAssistantMcpConnectionOwners.User && connection.AllowBackgroundUse)
            .Select(connection => connection.Id)
            .ToArray();
        var remove = connections.Select(connection => connection.Id).Except(keep).ToArray();
        if (remove.Length > 0)
        {
            await context.McpConnections.Where(connection => remove.Contains(connection.Id)).ExecuteDeleteAsync(cancellationToken);
        }
        if (keep.Length > 0)
        {
            var now = DateTimeOffset.UtcNow;
            await context.McpConnections
                .Where(connection => keep.Contains(connection.Id))
                .ExecuteUpdateAsync(setters => ClearTokens(setters, now), cancellationToken);
        }
        return connections;
    }

    /// <summary>The token connections of one owner kind, or of one owner, across servers.</summary>
    public async Task<IReadOnlyList<AssistantMcpConnection>> ListTokenConnectionsAsync(
        string ownerKind,
        string? ownerActorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .AsNoTracking()
            .Where(connection => connection.OwnerKind == ownerKind
                && connection.Status != null
                && (ownerActorId == null || connection.OwnerActorId == ownerActorId))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The token connections of a server per status, for the administration.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountTokenConnectionsAsync(
        string serverId,
        string ownerKind,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .Where(connection => connection.ServerId == serverId && connection.OwnerKind == ownerKind && connection.Status != null)
            .GroupBy(connection => connection.Status!)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, cancellationToken);
    }

    /// <summary>
    /// The token connections that cannot be used until someone acts, per owner kind and status, for
    /// the health check.
    /// </summary>
    public async Task<IReadOnlyList<(string OwnerKind, string Status, int Count)>> CountUnusableConnectionsAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var counts = await context.McpConnections
            .Where(connection => connection.Status != null && connection.Status != NhAssistantMcpConnectionStatuses.Active)
            .GroupBy(connection => new { connection.OwnerKind, connection.Status })
            .Select(group => new { group.Key.OwnerKind, group.Key.Status, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return [.. counts.Select(item => (item.OwnerKind, item.Status!, item.Count))];
    }

    /// <summary>Personal token connections unused since <paramref name="before"/>.</summary>
    public async Task<IReadOnlyList<AssistantMcpConnection>> ListStaleUserConnectionsAsync(
        DateTimeOffset before,
        int take,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections
            .AsNoTracking()
            .Where(connection => connection.OwnerKind == NhAssistantMcpConnectionOwners.User
                && connection.Status != null
                && (connection.LastUsedAt ?? connection.ConnectedAt ?? connection.CreatedAt) < before)
            .OrderBy(connection => connection.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the tokens of a personal connection when it is still unused since
    /// <paramref name="before"/>; returns the row as it was, for revocation, or null when it was used
    /// or removed meanwhile. As for a disconnect (<see cref="DeleteTokenConnectionsAsync"/>), a
    /// person's consent to background use stays on the row without tokens; a row without consent is
    /// deleted.
    /// </summary>
    public async Task<AssistantMcpConnection?> DeleteIfStaleAsync(Guid id, DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var connection = await context.McpConnections
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id
                && row.OwnerKind == NhAssistantMcpConnectionOwners.User
                && row.Status != null
                && (row.LastUsedAt ?? row.ConnectedAt ?? row.CreatedAt) < before, cancellationToken);
        if (connection is null)
        {
            return null;
        }

        var stale = context.McpConnections
            .Where(row => row.Id == id && row.Status != null && (row.LastUsedAt ?? row.ConnectedAt ?? row.CreatedAt) < before);
        int changed;
        if (connection.AllowBackgroundUse)
        {
            var now = DateTimeOffset.UtcNow;
            changed = await stale.ExecuteUpdateAsync(setters => ClearTokens(setters, now), cancellationToken);
        }
        else
        {
            changed = await stale.ExecuteDeleteAsync(cancellationToken);
        }
        return changed == 1 ? connection : null;
    }

    /// <summary>
    /// Clears every token and connection field of a row, keeping the person's consent to background
    /// use.
    /// </summary>
    private static void ClearTokens(Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<AssistantMcpConnection> setters, DateTimeOffset now)
    {
        setters
            .SetProperty(connection => connection.Status, (string?)null)
            .SetProperty(connection => connection.Issuer, (string?)null)
            .SetProperty(connection => connection.ClientId, (string?)null)
            .SetProperty(connection => connection.Resource, (string?)null)
            .SetProperty(connection => connection.GrantedScopes, (string?)null)
            .SetProperty(connection => connection.RequiredScopes, (string?)null)
            .SetProperty(connection => connection.ProtectedAccessToken, (string?)null)
            .SetProperty(connection => connection.ProtectedRefreshToken, (string?)null)
            .SetProperty(connection => connection.AccessTokenExpiresAt, (DateTimeOffset?)null)
            .SetProperty(connection => connection.RefreshTokenExpiresAt, (DateTimeOffset?)null)
            .SetProperty(connection => connection.ConnectedBy, (string?)null)
            .SetProperty(connection => connection.ConnectedAt, (DateTimeOffset?)null)
            .SetProperty(connection => connection.LastFailureCode, (string?)null)
            .SetProperty(connection => connection.RefreshLeaseUntil, (DateTimeOffset?)null)
            .SetProperty(connection => connection.RefreshLeaseOwner, (string?)null)
            .SetProperty(connection => connection.UpdatedAt, now)
            .SetProperty(connection => connection.Version, connection => connection.Version + 1);
    }

    public async Task<bool> DeleteConnectionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpConnections.Where(connection => connection.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;
    }

    // ----- Authorization flows -----

    public async Task AddFlowAsync(AssistantMcpAuthorizationFlow flow, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        context.McpAuthorizationFlows.Add(flow);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CountPendingFlowsAsync(string startedBy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpAuthorizationFlows.CountAsync(
            flow => flow.StartedBy == startedBy
                && (flow.Status == FlowPending || flow.Status == FlowExchanged)
                && flow.ExpiresAt > now,
            cancellationToken);
    }

    public async Task<AssistantMcpAuthorizationFlow?> FindFlowByStateHashAsync(string stateHash, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpAuthorizationFlows.AsNoTracking().SingleOrDefaultAsync(flow => flow.StateHash == stateHash, cancellationToken);
    }

    public async Task<AssistantMcpAuthorizationFlow?> FindFlowAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpAuthorizationFlows.AsNoTracking().SingleOrDefaultAsync(flow => flow.Id == id, cancellationToken);
    }

    /// <summary>
    /// Moves a flow from <paramref name="fromStatus"/> on, once: a flow that another request moved
    /// first is not changed again.
    /// </summary>
    public async Task<bool> TryTransitionFlowAsync(
        Guid id,
        string fromStatus,
        Action<AssistantMcpAuthorizationFlow> apply,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var flow = await context.McpAuthorizationFlows.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (flow is null || flow.Status != fromStatus)
        {
            return false;
        }
        apply(flow);
        flow.Version++;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    public async Task<int> DeleteFlowsExpiredBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpAuthorizationFlows.Where(flow => flow.ExpiresAt < before).ExecuteDeleteAsync(cancellationToken);
    }

    // ----- Client registrations -----

    public async Task<AssistantMcpClientRegistration?> FindRegistrationAsync(string serverId, string issuerHash, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.McpClientRegistrations
            .AsNoTracking()
            .SingleOrDefaultAsync(registration => registration.ServerId == serverId && registration.IssuerHash == issuerHash, cancellationToken);
    }

    /// <summary>Stores a registration; a concurrent registration of the same issuer wins and is returned.</summary>
    public async Task<AssistantMcpClientRegistration> AddRegistrationAsync(
        AssistantMcpClientRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var context = contextFactory.CreateDbContext();
            context.McpClientRegistrations.Add(registration);
            await context.SaveChangesAsync(cancellationToken);
            return registration;
        }
        catch (DbUpdateException exception)
        {
            // A concurrent registration of the same issuer wins; without one the write itself failed.
            return await FindRegistrationAsync(registration.ServerId, registration.IssuerHash, cancellationToken)
                ?? throw new InvalidOperationException("The client registration could not be stored.", exception);
        }
    }

    public async Task DeleteRegistrationsAsync(string serverId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.McpClientRegistrations.Where(registration => registration.ServerId == serverId).ExecuteDeleteAsync(cancellationToken);
    }
}
