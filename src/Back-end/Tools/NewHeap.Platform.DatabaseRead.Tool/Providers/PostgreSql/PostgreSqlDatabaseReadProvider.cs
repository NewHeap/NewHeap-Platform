using System.Data.Common;
using Npgsql;

namespace NewHeap.Platform.DatabaseRead;

internal sealed class PostgreSqlDatabaseReadProvider : IDatabaseReadProvider
{
    private const string ReadOnlyVerificationSql =
        """
        SELECT
            NOT role.rolsuper AND
            NOT role.rolcreaterole AND
            NOT role.rolcreatedb AND
            NOT role.rolreplication AND
            NOT role.rolbypassrls AND
            NOT has_database_privilege(current_user, current_database(), 'CREATE') AND
            NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS candidate
                WHERE candidate.relkind IN ('r', 'p', 'f') AND (
                    has_table_privilege(current_user, candidate.oid, 'INSERT') OR
                    has_table_privilege(current_user, candidate.oid, 'UPDATE') OR
                    has_table_privilege(current_user, candidate.oid, 'DELETE') OR
                    has_table_privilege(current_user, candidate.oid, 'TRUNCATE') OR
                    has_table_privilege(current_user, candidate.oid, 'TRIGGER')
                )
            ) AND
            NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_namespace AS candidate
                WHERE candidate.nspname NOT LIKE 'pg_temp_%' AND
                      candidate.nspname NOT LIKE 'pg_toast_temp_%' AND
                      has_schema_privilege(current_user, candidate.oid, 'CREATE')
            ) AND
            NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_proc AS candidate
                INNER JOIN pg_catalog.pg_namespace AS candidate_namespace
                    ON candidate_namespace.oid = candidate.pronamespace
                WHERE candidate_namespace.nspname NOT LIKE 'pg_%' AND
                      candidate_namespace.nspname <> 'information_schema' AND
                      has_function_privilege(current_user, candidate.oid, 'EXECUTE')
            )
        FROM pg_catalog.pg_roles AS role
        WHERE role.rolname = current_user;
        """;

    public string Name => "postgresql";

    public DbConnection CreateConnection(
        string connectionString,
        string requestId,
        DatabaseReadLimits limits)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"NewHeap database read {requestId}",
            Timeout = Math.Min(limits.TimeoutSeconds, 30),
            CommandTimeout = limits.TimeoutSeconds
        };

        return new NpgsqlConnection(builder.ConnectionString);
    }

    public async Task<bool> VerifyReadOnlyPrincipalAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ReadOnlyVerificationSql;
        command.CommandTimeout = limits.TimeoutSeconds;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is true;
    }

    public async Task ConfigureReadOnlyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using (var readOnlyCommand = connection.CreateCommand())
        {
            readOnlyCommand.Transaction = transaction;
            readOnlyCommand.CommandText = "SET TRANSACTION READ ONLY;";
            readOnlyCommand.CommandTimeout = limits.TimeoutSeconds;
            await readOnlyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var timeoutCommand = connection.CreateCommand();
        timeoutCommand.Transaction = transaction;
        timeoutCommand.CommandText =
            "SELECT set_config('statement_timeout', @statementTimeout, true), " +
            "set_config('lock_timeout', @lockTimeout, true);";
        timeoutCommand.CommandTimeout = limits.TimeoutSeconds;

        var statementTimeout = timeoutCommand.CreateParameter();
        statementTimeout.ParameterName = "@statementTimeout";
        statementTimeout.Value = $"{limits.TimeoutSeconds * 1000}ms";
        timeoutCommand.Parameters.Add(statementTimeout);

        var lockTimeout = timeoutCommand.CreateParameter();
        lockTimeout.ParameterName = "@lockTimeout";
        lockTimeout.Value = $"{limits.LockTimeoutMilliseconds}ms";
        timeoutCommand.Parameters.Add(lockTimeout);

        await timeoutCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
