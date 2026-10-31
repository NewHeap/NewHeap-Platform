using System.Data;
using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// The requests of one long-lived process. The profile and connection string are resolved once.
/// Every request still opens and disposes its connection, but over a pool with one stable
/// connection string: the driver hands back the same physical connection and resets its session
/// state first, so nothing a request did reaches the next one, and a broken connection is replaced
/// without any code here.
/// </summary>
/// <remarks>
/// The principal is verified on a physical connection the session has not verified yet, and again
/// when that verification is older than <see cref="DatabaseReadSessionOptions.VerificationInterval"/>.
/// One request runs at a time, so the pool holds one connection.
/// </remarks>
internal sealed class DatabaseReadSessionHost(
    string? profilesPath,
    string? environmentName,
    IDatabaseReadConnectionFactory connectionFactory,
    TimeSpan verificationInterval,
    TimeProvider timeProvider) : IDatabaseReadRequestHost, IAsyncDisposable
{
    /// <summary>
    /// The connection string is the key of the driver's pool, so it cannot follow a request's
    /// timeout as the one-shot string does. The request's timeout bounds the open separately.
    /// </summary>
    private const int ConnectTimeoutSeconds = 30;

    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, ResolvedDatabaseReadProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _connectionStrings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SessionPool> _pools = new(StringComparer.Ordinal);
    private VerifiedConnection? _verified;

    public TimeProvider TimeProvider => timeProvider;

    public async Task<ResolvedDatabaseReadProfile> LoadProfileAsync(
        string? profileName,
        CancellationToken cancellationToken)
    {
        var key = profileName ?? string.Empty;
        if (_profiles.TryGetValue(key, out var profile))
        {
            return profile;
        }

        profile = await DatabaseReadProfileLoader.LoadAsync(
            profileName,
            environmentName,
            profilesPath,
            cancellationToken);
        _profiles.Add(key, profile);

        return profile;
    }

    public string ResolveConnectionString(ResolvedDatabaseReadProfile profile)
    {
        if (_connectionStrings.TryGetValue(profile.Name, out var connectionString))
        {
            return connectionString;
        }

        connectionString = NewHeapConnectionStringResolver.Resolve(profile);
        _connectionStrings.Add(profile.Name, connectionString);

        return connectionString;
    }

    public async Task<DbConnection> OpenVerifiedConnectionAsync(
        IDatabaseReadProvider provider,
        DatabaseReadExecutionContext executionContext,
        string connectionString,
        string requestId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        executionContext.Enter(DatabaseReadExecutionStage.ConnectionOpen);
        var reusableConnectionString = CreateReusableConnectionString(provider, connectionString);
        var connection = await OpenWithinTimeoutAsync(provider, reusableConnectionString, limits, cancellationToken);

        try
        {
            provider.EnsureSupportedServer(connection);

            executionContext.Enter(DatabaseReadExecutionStage.ReadOnlyVerification);
            await EnsureVerifiedAsync(provider, connection, reusableConnectionString, limits, cancellationToken);

            return connection;
        }
        catch
        {
            ReportIfConnectionIsLost(connection, executionContext);
            await connection.DisposeAsync();

            throw;
        }
    }

    /// <summary>
    /// A connection from the pool can have died since the last request. Its end rolled the
    /// transaction back, and rolling back again would replace the request's own failure.
    /// </summary>
    public async Task RollBackAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadExecutionContext executionContext)
    {
        if (ReportIfConnectionIsLost(connection, executionContext))
        {
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None);
    }

    /// <summary>
    /// The next request starts with a verification, whatever identifier the server gives its new
    /// connection, and resolves the configuration again, because what failed may be a credential
    /// that was rotated.
    /// </summary>
    public void OnConnectionFailed()
    {
        _profiles.Clear();
        _connectionStrings.Clear();
        _verified = null;
    }

    /// <summary>
    /// Closes the session's connections, so the server sees them end instead of finding them gone.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools.Values)
        {
            await using var connection = pool.Provider.CreateConnection(
                pool.ConnectionString,
                _sessionId,
                ConnectionLimits(pool.Limits));
            await pool.Provider.ClearConnectionPoolAsync(connection, CancellationToken.None);
        }

        _pools.Clear();
    }

    /// <summary>
    /// A connection that was gone before the statement started is a failed open, whatever the driver
    /// reports for it, such as the SQLSTATE of a terminated PostgreSQL backend: the next request gets
    /// a new connection, so the caller should hear that it may try again.
    /// </summary>
    private bool ReportIfConnectionIsLost(DbConnection connection, DatabaseReadExecutionContext executionContext)
    {
        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        if (executionContext.Stage == DatabaseReadExecutionStage.ReadOnlyVerification)
        {
            executionContext.Enter(DatabaseReadExecutionStage.ConnectionOpen);
        }

        OnConnectionFailed();

        return true;
    }

    private static string CreateReusableConnectionString(IDatabaseReadProvider provider, string connectionString)
    {
        try
        {
            return provider.CreateReusableConnectionString(connectionString);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw DatabaseReadConnectionFactory.InvalidConfiguration();
        }
    }

    private static DatabaseReadLimits ConnectionLimits(DatabaseReadLimits limits)
    {
        return limits with { TimeoutSeconds = ConnectTimeoutSeconds };
    }

    /// <summary>
    /// A new physical connection may take as long as the connection string allows, but a request
    /// never waits longer than its own timeout. Running out of it is the failure a one-shot command
    /// reports when its shorter connect timeout expires.
    /// </summary>
    private async Task<DbConnection> OpenWithinTimeoutAsync(
        IDatabaseReadProvider provider,
        string reusableConnectionString,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        _pools.TryAdd(reusableConnectionString, new SessionPool(provider, reusableConnectionString, limits));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(limits.TimeoutSeconds), timeProvider);
        using var open = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await connectionFactory.OpenAsync(
                provider,
                reusableConnectionString,
                _sessionId,
                ConnectionLimits(limits),
                open.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DatabaseReadConnectTimeoutException();
        }
    }

    private async Task EnsureVerifiedAsync(
        IDatabaseReadProvider provider,
        DbConnection connection,
        string reusableConnectionString,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var physicalConnectionId = provider.GetPhysicalConnectionId(connection);
        if (_verified is { } verified &&
            verified.ConnectionString == reusableConnectionString &&
            verified.PhysicalConnectionId == physicalConnectionId &&
            timeProvider.GetUtcNow() - verified.VerifiedAt < verificationInterval)
        {
            return;
        }

        _verified = null;

        try
        {
            await DatabaseReadPrincipal.EnsureReadOnlyAsync(provider, connection, limits, cancellationToken);
        }
        catch
        {
            // The verification changes session state, such as the active roles on MySQL and
            // MariaDB, and restores it at the end. It may not have reached that end.
            await provider.ClearConnectionPoolAsync(connection, CancellationToken.None);

            throw;
        }

        _verified = new VerifiedConnection(reusableConnectionString, physicalConnectionId, timeProvider.GetUtcNow());
    }

    private sealed record VerifiedConnection(
        string ConnectionString,
        string PhysicalConnectionId,
        DateTimeOffset VerifiedAt);

    private sealed record SessionPool(
        IDatabaseReadProvider Provider,
        string ConnectionString,
        DatabaseReadLimits Limits);
}

/// <summary>
/// A request's timeout ran out while its connection opened. The provider classifies it as it
/// classifies any other failure at that stage.
/// </summary>
internal sealed class DatabaseReadConnectTimeoutException : DbException;
