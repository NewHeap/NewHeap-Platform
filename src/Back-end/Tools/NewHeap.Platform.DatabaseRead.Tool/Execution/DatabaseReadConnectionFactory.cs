using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

internal interface IDatabaseReadConnectionFactory
{
    Task<DbConnection> OpenAsync(
        IDatabaseReadProvider provider,
        string connectionString,
        string requestId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken);
}

internal sealed class DatabaseReadConnectionFactory : IDatabaseReadConnectionFactory
{
    public static DatabaseReadConnectionFactory Instance { get; } = new();

    private DatabaseReadConnectionFactory()
    {
    }

    public async Task<DbConnection> OpenAsync(
        IDatabaseReadProvider provider,
        string connectionString,
        string requestId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        DbConnection connection;

        try
        {
            connection = provider.CreateConnection(connectionString, requestId, limits);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new DatabaseReadExpectedException(
                "connection-configuration-invalid",
                "The selected environment's resolved connection string is not valid for the configured provider.",
                DatabaseReadExitCode.InvalidProfile);
        }

        try
        {
            await connection.OpenAsync(cancellationToken);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }
}