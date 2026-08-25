using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace NewHeap.Platform.DatabaseRead;

internal sealed class SqlServerDatabaseReadProvider : IDatabaseReadProvider
{
    private const string ReadOnlyVerificationSql =
        """
        SELECT CONVERT(bit, CASE WHEN
            COALESCE(IS_SRVROLEMEMBER('sysadmin'), 0) = 1 OR
            COALESCE(IS_MEMBER('db_owner'), 0) = 1 OR
            COALESCE(IS_MEMBER('db_accessadmin'), 0) = 1 OR
            COALESCE(IS_MEMBER('db_securityadmin'), 0) = 1 OR
            COALESCE(IS_MEMBER('db_backupoperator'), 0) = 1 OR
            COALESCE(IS_MEMBER('db_datawriter'), 0) = 1 OR
            COALESCE(IS_MEMBER('db_ddladmin'), 0) = 1 OR
            HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER') = 1 OR
            HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN') = 1 OR
            HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY DATABASE') = 1 OR
            HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') = 1 OR
            HAS_PERMS_BY_NAME(NULL, NULL, 'ADMINISTER BULK OPERATIONS') = 1 OR
            HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 1 OR
            HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1 OR
            HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 OR
            HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE PROCEDURE') = 1 OR
            HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'EXECUTE') = 1 OR
            EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('U', 'V') AND
                    candidate.is_ms_shipped = 0 AND (
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT',
                        'INSERT') = 1 OR
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT',
                        'UPDATE') = 1 OR
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT',
                        'DELETE') = 1
                    )
            ) OR
            EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('P', 'PC') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT',
                        'EXECUTE') = 1
            ) OR
            EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('FN', 'IF', 'TF', 'FS', 'FT') AND
                    candidate.is_ms_shipped = 0 AND (
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT',
                        'EXECUTE') = 1 OR
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT',
                        'SELECT') = 1
                    )
            )
        THEN 0 ELSE 1 END);
        """;

    public string Name => "sql-server";

    public DbConnection CreateConnection(
        string connectionString,
        string requestId,
        DatabaseReadLimits limits)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = $"NewHeap database read {requestId}",
            ConnectTimeout = Math.Min(limits.TimeoutSeconds, 30)
        };

        return new SqlConnection(builder.ConnectionString);
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
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FormattableString.Invariant(
            $"SET LOCK_TIMEOUT {limits.LockTimeoutMilliseconds};");
        command.CommandTimeout = limits.TimeoutSeconds;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
