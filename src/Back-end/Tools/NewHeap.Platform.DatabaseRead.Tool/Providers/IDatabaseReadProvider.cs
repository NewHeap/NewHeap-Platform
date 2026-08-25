using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

internal interface IDatabaseReadProvider
{
    string Name { get; }

    DbConnection CreateConnection(
        string connectionString,
        string requestId,
        DatabaseReadLimits limits);

    Task<bool> VerifyReadOnlyPrincipalAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);

    Task ConfigureReadOnlyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);
}

internal static class DatabaseReadProviderFactory
{
    public static IDatabaseReadProvider Create(DatabaseProviderKind provider)
    {
        return provider switch
        {
            DatabaseProviderKind.SqlServer => new SqlServerDatabaseReadProvider(),
            DatabaseProviderKind.PostgreSql => new PostgreSqlDatabaseReadProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
        };
    }
}
