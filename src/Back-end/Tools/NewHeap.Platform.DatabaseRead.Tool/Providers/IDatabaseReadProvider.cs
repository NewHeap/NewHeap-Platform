using System.Data;
using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

internal interface IDatabaseReadProvider
{
    string Name { get; }

    DbConnection CreateConnection(
        string connectionString,
        string requestId,
        DatabaseReadLimits limits);

    /// <summary>
    /// Checks the server right after the connection opened, from what the server reported while
    /// the connection opened and before any statement runs, so that a server the verification
    /// cannot read stops with a profile error instead of failing on a statement it does not know.
    /// </summary>
    void EnsureSupportedServer(DbConnection connection);

    Task<DatabaseReadPrincipalVerification> VerifyReadOnlyPrincipalAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts the transaction that the diagnostic runs in, at read committed. A provider that can
    /// start a transaction read only does so here, because a transaction that has started can no
    /// longer change that; the others make it read only in
    /// <see cref="ConfigureReadOnlyTransactionAsync"/>.
    /// </summary>
    Task<DbTransaction> BeginReadOnlyTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken);

    Task ConfigureReadOnlyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);

    Task<DatabaseSchemaResultResponse> ReadSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResolvedDatabaseSchemaRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);

    DatabaseReadProviderFailure ClassifyException(
        DbException exception,
        DatabaseReadExecutionStage stage);
}

internal static class DatabaseReadProviderFactory
{
    public static IDatabaseReadProvider Create(ResolvedDatabaseReadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Provider == DatabaseProviderKind.SqlServer
            ? new SqlServerDatabaseReadProvider(profile.SqlServerFunctionPolicy)
            : Create(profile.Provider);
    }

    public static IDatabaseReadProvider Create(DatabaseProviderKind provider)
    {
        return provider switch
        {
            DatabaseProviderKind.SqlServer => new SqlServerDatabaseReadProvider(),
            DatabaseProviderKind.PostgreSql => new PostgreSqlDatabaseReadProvider(),
            DatabaseProviderKind.MySql => new MySqlDatabaseReadProvider(DatabaseProviderKind.MySql),
            DatabaseProviderKind.MariaDb => new MySqlDatabaseReadProvider(DatabaseProviderKind.MariaDb),
            DatabaseProviderKind.MySqlCompatible => new MySqlDatabaseReadProvider(DatabaseProviderKind.MySqlCompatible),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
    }
}

internal static class DatabaseReadTransactions
{
    /// <summary>
    /// The transaction of SQL Server and PostgreSQL, which their providers make read only, or keep
    /// read only through the principal, after it started.
    /// </summary>
    public static async Task<DbTransaction> BeginReadCommittedAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        return await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }
}

internal sealed record DatabaseReadPrincipalVerification(bool IsReadOnly, string? FailedCheck = null);
