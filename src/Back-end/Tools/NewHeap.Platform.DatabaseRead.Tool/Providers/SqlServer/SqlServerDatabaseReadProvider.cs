using System.Data.Common;
using System.Globalization;
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

    public Task<DatabaseSchemaResultResponse> ReadSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResolvedDatabaseSchemaRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        return request.Operation switch
        {
            DatabaseSchemaOperation.Search => SearchSchemaAsync(
                connection,
                transaction,
                request,
                limits,
                cancellationToken),
            DatabaseSchemaOperation.Describe => DescribeSchemaAsync(
                connection,
                transaction,
                request,
                limits,
                cancellationToken),
            DatabaseSchemaOperation.Indexes => ReadIndexesSchemaAsync(
                connection,
                transaction,
                request,
                limits,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Operation, null)
        };
    }

    public DatabaseReadProviderFailure ClassifyException(DbException exception)
    {
        if (exception is SqlException sqlException)
        {
            foreach (SqlError error in sqlException.Errors)
            {
                var failure = ClassifySqlError(error.Number);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return Failure(
            "database-failure",
            null,
            false,
            "The database rejected or could not complete the diagnostic operation.");
    }

    private static DatabaseReadProviderFailure? ClassifySqlError(int number)
    {
        var providerCode = number.ToString(CultureInfo.InvariantCulture);
        return number switch
        {
            208 => Failure("object-not-found", providerCode, false,
                "A referenced database object does not exist."),
            207 => Failure("column-not-found", providerCode, false,
                "A referenced database column does not exist."),
            229 => Failure("permission-denied", providerCode, false,
                "The database principal is not permitted to read a referenced object or column."),
            102 or 156 => Failure("syntax-error", providerCode, false,
                "The database rejected the SQL syntax."),
            -2 => Failure("statement-timeout", providerCode, false,
                "The database cancelled the statement after its configured execution boundary."),
            1222 => Failure("lock-timeout", providerCode, false,
                "The database could not acquire a required lock within the configured boundary."),
            1205 => Failure("deadlock", providerCode, true,
                "The database selected the diagnostic statement as a deadlock victim."),
            4060 => Failure("database-not-found", providerCode, false,
                "The configured database does not exist or is unavailable to the principal."),
            18456 => Failure("authentication-failed", providerCode, false,
                "The database rejected the configured principal."),
            53 or 64 or 233 or 10053 or 10054 or 10060 => Failure(
                "connection-failed",
                providerCode,
                true,
                "The database connection could not be established or was interrupted."),
            _ => null
        };
    }

    private static async Task<DatabaseSchemaResultResponse> SearchSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResolvedDatabaseSchemaRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT TOP (@maximumObjects)
                   candidate_schema.name,
                   candidate.name,
                   CASE candidate.type
                       WHEN 'U' THEN 'table'
                       WHEN 'V' THEN 'view'
                       ELSE 'object'
                   END
            FROM sys.objects AS candidate
            INNER JOIN sys.schemas AS candidate_schema
                ON candidate_schema.schema_id = candidate.schema_id
            WHERE candidate.type IN ('U', 'V')
              AND candidate.is_ms_shipped = 0
              AND (
                  HAS_PERMS_BY_NAME(
                      QUOTENAME(candidate_schema.name) + '.' + QUOTENAME(candidate.name),
                      'OBJECT',
                      'SELECT') = 1 OR
                  EXISTS (
                      SELECT 1
                      FROM sys.columns AS selectable_column
                      WHERE selectable_column.object_id = candidate.object_id
                        AND HAS_PERMS_BY_NAME(
                            QUOTENAME(candidate_schema.name) + '.' +
                            QUOTENAME(candidate.name) + '.' +
                            QUOTENAME(selectable_column.name),
                            'COLUMN',
                            'SELECT') = 1
                  )
              )
              AND (@schemaName IS NULL OR candidate_schema.name = @schemaName)
              AND (@searchTerm IS NULL OR CHARINDEX(@searchTerm, candidate.name) > 0)
            ORDER BY candidate_schema.name, candidate.name;
            """;
        AddParameter(command, "@maximumObjects", limits.MaximumRows + 1);
        AddParameter(command, "@schemaName", request.SchemaName);
        AddParameter(command, "@searchTerm", request.SearchTerm);

        var objects = new List<DatabaseSchemaObjectSummaryResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = reader.GetString(0);
            var objectName = reader.GetString(1);
            objects.Add(new DatabaseSchemaObjectSummaryResponse
            {
                Schema = schemaName,
                Name = objectName,
                Kind = reader.GetString(2),
                SqlIdentifier = Quote(schemaName, objectName)
            });
        }

        var truncated = objects.Count > limits.MaximumRows;
        if (truncated)
        {
            objects.RemoveAt(objects.Count - 1);
        }

        return new DatabaseSchemaResultResponse
        {
            Operation = "search",
            Objects = objects,
            Truncated = truncated
        };
    }

    private static async Task<DatabaseSchemaResultResponse> DescribeSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResolvedDatabaseSchemaRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var descriptor = await ReadObjectDescriptorAsync(
            connection,
            transaction,
            request.SchemaName!,
            request.ObjectName!,
            limits,
            cancellationToken);
        var columns = await ReadColumnsAsync(
            connection,
            transaction,
            descriptor.ObjectId,
            descriptor.Schema,
            descriptor.Name,
            limits,
            cancellationToken);
        var indexes = await ReadIndexesAsync(
            connection,
            transaction,
            descriptor.ObjectId,
            descriptor.Schema,
            descriptor.Name,
            limits,
            cancellationToken);

        return new DatabaseSchemaResultResponse
        {
            Operation = "describe",
            Object = new DatabaseSchemaObjectResponse
            {
                Schema = descriptor.Schema,
                Name = descriptor.Name,
                Kind = descriptor.Kind,
                SqlIdentifier = Quote(descriptor.Schema, descriptor.Name),
                Columns = columns,
                Indexes = indexes
            }
        };
    }

    private static async Task<DatabaseSchemaResultResponse> ReadIndexesSchemaAsync(
        DbConnection connection,
        DbTransaction transaction,
        ResolvedDatabaseSchemaRequest request,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var descriptor = await ReadObjectDescriptorAsync(
            connection,
            transaction,
            request.SchemaName!,
            request.ObjectName!,
            limits,
            cancellationToken);
        var indexes = await ReadIndexesAsync(
            connection,
            transaction,
            descriptor.ObjectId,
            descriptor.Schema,
            descriptor.Name,
            limits,
            cancellationToken);

        return new DatabaseSchemaResultResponse
        {
            Operation = "indexes",
            Indexes = new DatabaseSchemaIndexesResponse
            {
                Schema = descriptor.Schema,
                Name = descriptor.Name,
                Kind = descriptor.Kind,
                SqlIdentifier = Quote(descriptor.Schema, descriptor.Name),
                Items = indexes
            }
        };
    }

    private static async Task<SqlServerObjectDescriptor> ReadObjectDescriptorAsync(
        DbConnection connection,
        DbTransaction transaction,
        string schemaName,
        string objectName,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT TOP (1)
                   candidate.object_id,
                   candidate_schema.name,
                   candidate.name,
                   CASE candidate.type
                       WHEN 'U' THEN 'table'
                       WHEN 'V' THEN 'view'
                       ELSE 'object'
                   END
            FROM sys.objects AS candidate
            INNER JOIN sys.schemas AS candidate_schema
                ON candidate_schema.schema_id = candidate.schema_id
            WHERE candidate.type IN ('U', 'V')
              AND candidate.is_ms_shipped = 0
              AND candidate_schema.name = @schemaName
              AND candidate.name = @objectName
              AND (
                  HAS_PERMS_BY_NAME(
                      QUOTENAME(candidate_schema.name) + '.' + QUOTENAME(candidate.name),
                      'OBJECT',
                      'SELECT') = 1 OR
                  EXISTS (
                      SELECT 1
                      FROM sys.columns AS selectable_column
                      WHERE selectable_column.object_id = candidate.object_id
                        AND HAS_PERMS_BY_NAME(
                            QUOTENAME(candidate_schema.name) + '.' +
                            QUOTENAME(candidate.name) + '.' +
                            QUOTENAME(selectable_column.name),
                            'COLUMN',
                            'SELECT') = 1
                  )
              );
            """;
        AddParameter(command, "@schemaName", schemaName);
        AddParameter(command, "@objectName", objectName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DatabaseReadExpectedException(
                "schema-object-not-found",
                "The database object was not found or is not selectable by the configured principal.",
                DatabaseReadExitCode.DatabaseFailure);
        }

        return new SqlServerObjectDescriptor(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static async Task<IReadOnlyList<DatabaseSchemaColumnResponse>> ReadColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        int objectId,
        string schemaName,
        string objectName,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT candidate.column_id,
                   candidate.name,
                   candidate_type.name,
                   candidate.is_nullable,
                   CONVERT(bit, CASE WHEN EXISTS (
                       SELECT 1
                       FROM sys.indexes AS primary_index
                       INNER JOIN sys.index_columns AS primary_column
                           ON primary_column.object_id = primary_index.object_id
                          AND primary_column.index_id = primary_index.index_id
                       WHERE primary_index.object_id = candidate.object_id
                         AND primary_index.is_primary_key = 1
                         AND primary_column.column_id = candidate.column_id
                   ) THEN 1 ELSE 0 END)
            FROM sys.columns AS candidate
            INNER JOIN sys.types AS candidate_type
                ON candidate_type.user_type_id = candidate.user_type_id
            WHERE candidate.object_id = @objectId
              AND (
                  HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') = 1 OR
                  HAS_PERMS_BY_NAME(
                      @qualifiedObjectName + '.' + QUOTENAME(candidate.name),
                      'COLUMN',
                      'SELECT') = 1
              )
            ORDER BY candidate.column_id;
            """;
        AddParameter(command, "@objectId", objectId);
        AddParameter(command, "@qualifiedObjectName", Quote(schemaName, objectName));

        var columns = new List<DatabaseSchemaColumnResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new DatabaseSchemaColumnResponse
            {
                Ordinal = reader.GetInt32(0),
                Name = reader.GetString(1),
                ProviderType = reader.GetString(2),
                AllowsNull = reader.GetBoolean(3),
                IsPrimaryKey = reader.GetBoolean(4)
            });
        }

        return columns;
    }

    private static async Task<IReadOnlyList<DatabaseSchemaIndexResponse>> ReadIndexesAsync(
        DbConnection connection,
        DbTransaction transaction,
        int objectId,
        string schemaName,
        string objectName,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT candidate.name,
                   candidate.is_unique,
                   candidate.is_primary_key,
                   candidate.has_filter,
                   indexed_column.key_ordinal,
                   indexed_column.index_column_id,
                   indexed_column.is_included_column,
                   indexed_column.is_descending_key,
                   schema_column.name
            FROM sys.indexes AS candidate
            INNER JOIN sys.index_columns AS indexed_column
                ON indexed_column.object_id = candidate.object_id
               AND indexed_column.index_id = candidate.index_id
            INNER JOIN sys.columns AS schema_column
                ON schema_column.object_id = indexed_column.object_id
               AND schema_column.column_id = indexed_column.column_id
            WHERE candidate.object_id = @objectId
              AND candidate.name IS NOT NULL
              AND candidate.is_hypothetical = 0
              AND indexed_column.column_id > 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM sys.index_columns AS denied_indexed_column
                  INNER JOIN sys.columns AS denied_schema_column
                      ON denied_schema_column.object_id = denied_indexed_column.object_id
                     AND denied_schema_column.column_id = denied_indexed_column.column_id
                  WHERE denied_indexed_column.object_id = candidate.object_id
                    AND denied_indexed_column.index_id = candidate.index_id
                    AND denied_indexed_column.column_id > 0
                    AND HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') <> 1
                    AND HAS_PERMS_BY_NAME(
                        @qualifiedObjectName + '.' + QUOTENAME(denied_schema_column.name),
                        'COLUMN',
                        'SELECT') <> 1
              )
              AND (
                  HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') = 1 OR
                  HAS_PERMS_BY_NAME(
                      @qualifiedObjectName + '.' + QUOTENAME(schema_column.name),
                      'COLUMN',
                      'SELECT') = 1
              )
            ORDER BY candidate.is_primary_key DESC,
                     candidate.name,
                     indexed_column.is_included_column,
                     CASE
                         WHEN indexed_column.is_included_column = 0
                         THEN indexed_column.key_ordinal
                         ELSE indexed_column.index_column_id
                     END;
            """;
        AddParameter(command, "@objectId", objectId);
        AddParameter(command, "@qualifiedObjectName", Quote(schemaName, objectName));

        var builders = new List<SqlServerIndexBuilder>();
        var byName = new Dictionary<string, SqlServerIndexBuilder>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var builder))
            {
                builder = new SqlServerIndexBuilder(
                    name,
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3));
                byName.Add(name, builder);
                builders.Add(builder);
            }

            var columnName = reader.GetString(8);
            if (reader.GetBoolean(6))
            {
                builder.IncludedColumns.Add(columnName);
            }
            else
            {
                builder.KeyColumns.Add(new DatabaseSchemaIndexColumnResponse
                {
                    Name = columnName,
                    Direction = reader.GetBoolean(7) ? "descending" : "ascending"
                });
            }
        }

        return builders
            .Select(builder => new DatabaseSchemaIndexResponse
            {
                Name = builder.Name,
                IsUnique = builder.IsUnique,
                IsPrimaryKey = builder.IsPrimaryKey,
                IsPartial = builder.IsPartial,
                Columns = builder.KeyColumns.Select(column => column.Name).ToArray(),
                KeyColumns = builder.KeyColumns,
                IncludedColumns = builder.IncludedColumns
            })
            .ToArray();
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(string schemaName, string objectName) =>
        $"[{schemaName.Replace("]", "]]", StringComparison.Ordinal)}]." +
        $"[{objectName.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static DatabaseReadProviderFailure Failure(
        string classification,
        string? providerCode,
        bool transient,
        string message) =>
        new("sql-server", classification, providerCode, transient, message);

    private sealed record SqlServerObjectDescriptor(
        int ObjectId,
        string Schema,
        string Name,
        string Kind);

    private sealed class SqlServerIndexBuilder(
        string name,
        bool isUnique,
        bool isPrimaryKey,
        bool isPartial)
    {
        public string Name { get; } = name;

        public bool IsUnique { get; } = isUnique;

        public bool IsPrimaryKey { get; } = isPrimaryKey;

        public bool IsPartial { get; } = isPartial;

        public List<DatabaseSchemaIndexColumnResponse> KeyColumns { get; } = [];

        public List<string> IncludedColumns { get; } = [];
    }
}
