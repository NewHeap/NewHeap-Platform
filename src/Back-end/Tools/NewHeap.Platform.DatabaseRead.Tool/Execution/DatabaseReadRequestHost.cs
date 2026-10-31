using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// What a request needs from the process it runs in. Every safety check of a request runs in the
/// same pipeline; a host only decides where the profile, the connection string and the verified
/// connection come from.
/// </summary>
internal interface IDatabaseReadRequestHost
{
    TimeProvider TimeProvider { get; }

    Task<ResolvedDatabaseReadProfile> LoadProfileAsync(
        string? profileName,
        CancellationToken cancellationToken);

    string ResolveConnectionString(ResolvedDatabaseReadProfile profile);

    /// <summary>
    /// An open connection on which the principal passed the read-only verification. The caller
    /// disposes it; a failure leaves no connection open.
    /// </summary>
    Task<DbConnection> OpenVerifiedConnectionAsync(
        IDatabaseReadProvider provider,
        DatabaseReadExecutionContext executionContext,
        string connectionString,
        string requestId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ends the request's transaction. It never commits: a request only reads.
    /// </summary>
    Task RollBackAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadExecutionContext executionContext);

    /// <summary>
    /// The request lost its connection or never got one.
    /// </summary>
    void OnConnectionFailed();
}

/// <summary>
/// A one-shot command: one request in one process, so the host loads everything anew and verifies
/// the principal on the only connection the process opens.
/// </summary>
internal sealed class DatabaseReadOneShotHost(
    CliOptions options,
    IDatabaseReadConnectionFactory connectionFactory) : IDatabaseReadRequestHost
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public Task RollBackAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadExecutionContext executionContext)
    {
        return transaction.RollbackAsync(CancellationToken.None);
    }

    public void OnConnectionFailed()
    {
    }

    public Task<ResolvedDatabaseReadProfile> LoadProfileAsync(
        string? profileName,
        CancellationToken cancellationToken)
    {
        return DatabaseReadProfileLoader.LoadAsync(
            profileName,
            options.EnvironmentName,
            options.ProfilesPath,
            cancellationToken);
    }

    public string ResolveConnectionString(ResolvedDatabaseReadProfile profile)
    {
        return NewHeapConnectionStringResolver.Resolve(profile);
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
        var connection = await connectionFactory.OpenAsync(
            provider,
            connectionString,
            requestId,
            limits,
            cancellationToken);

        try
        {
            provider.EnsureSupportedServer(connection);

            executionContext.Enter(DatabaseReadExecutionStage.ReadOnlyVerification);
            await DatabaseReadPrincipal.EnsureReadOnlyAsync(provider, connection, limits, cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }
}

internal static class DatabaseReadPrincipal
{
    /// <summary>
    /// Refuses the request unless the connection's principal passes the provider's read-only
    /// permission policy.
    /// </summary>
    public static async Task EnsureReadOnlyAsync(
        IDatabaseReadProvider provider,
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var verification = await provider.VerifyReadOnlyPrincipalAsync(connection, limits, cancellationToken);
        if (!verification.IsReadOnly)
        {
            throw new DatabaseReadExpectedException(
                "read-only-principal-not-verified",
                "The database principal did not pass the read-only permission policy.",
                DatabaseReadExitCode.PolicyRejected,
                verification.FailedCheck);
        }
    }
}
