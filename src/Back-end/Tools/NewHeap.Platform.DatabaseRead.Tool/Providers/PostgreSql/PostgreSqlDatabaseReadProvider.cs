using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

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
        if (exception is PostgresException postgresException)
        {
            return postgresException.SqlState switch
            {
                "42P01" => Failure("object-not-found", postgresException.SqlState, false,
                    "A referenced database object does not exist."),
                "42703" => Failure("column-not-found", postgresException.SqlState, false,
                    "A referenced database column does not exist."),
                "42501" => Failure("permission-denied", postgresException.SqlState, false,
                    "The database principal is not permitted to read a referenced object or column."),
                "42601" => Failure("syntax-error", postgresException.SqlState, false,
                    "The database rejected the SQL syntax."),
                "57014" => Failure("statement-timeout", postgresException.SqlState, false,
                    "The database cancelled the statement after its configured execution boundary."),
                "55P03" => Failure("lock-timeout", postgresException.SqlState, false,
                    "The database could not acquire a required lock within the configured boundary."),
                "40P01" => Failure("deadlock", postgresException.SqlState, true,
                    "The database selected the diagnostic statement as a deadlock victim."),
                "3D000" => Failure("database-not-found", postgresException.SqlState, false,
                    "The configured database does not exist or is unavailable to the principal."),
                "3F000" => Failure("schema-not-found", postgresException.SqlState, false,
                    "A referenced database schema does not exist."),
                _ when postgresException.SqlState.StartsWith("08", StringComparison.Ordinal) =>
                    Failure("connection-failed", postgresException.SqlState, true,
                        "The database connection could not be established or was interrupted."),
                _ => Failure("database-failure", null, false,
                    "The database rejected or could not complete the diagnostic operation.")
            };
        }

        return exception is NpgsqlException
            ? Failure("connection-failed", null, true,
                "The database connection could not be established or was interrupted.")
            : Failure("database-failure", null, false,
                "The database rejected or could not complete the diagnostic operation.");
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
            SELECT candidate_namespace.nspname,
                   candidate.relname,
                   CASE candidate.relkind
                       WHEN 'r' THEN 'table'
                       WHEN 'p' THEN 'partitioned-table'
                       WHEN 'v' THEN 'view'
                       WHEN 'm' THEN 'materialized-view'
                       WHEN 'f' THEN 'foreign-table'
                       ELSE 'object'
                   END
            FROM pg_catalog.pg_class AS candidate
            INNER JOIN pg_catalog.pg_namespace AS candidate_namespace
                ON candidate_namespace.oid = candidate.relnamespace
            WHERE candidate.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND candidate_namespace.nspname <> 'pg_catalog'
              AND candidate_namespace.nspname <> 'information_schema'
              AND candidate_namespace.nspname NOT LIKE 'pg_toast%'
              AND has_schema_privilege(current_user, candidate_namespace.oid, 'USAGE')
              AND (
                  has_table_privilege(current_user, candidate.oid, 'SELECT') OR
                  EXISTS (
                      SELECT 1
                      FROM pg_catalog.pg_attribute AS selectable_column
                      WHERE selectable_column.attrelid = candidate.oid
                        AND selectable_column.attnum > 0
                        AND NOT selectable_column.attisdropped
                        AND has_column_privilege(
                            current_user,
                            candidate.oid,
                            selectable_column.attname,
                            'SELECT')
                  )
              )
              AND (@schemaName IS NULL OR candidate_namespace.nspname = @schemaName)
              AND (@searchTerm IS NULL OR
                   strpos(lower(candidate.relname), lower(@searchTerm)) > 0)
            ORDER BY candidate_namespace.nspname, candidate.relname
            LIMIT @maximumObjects;
            """;
        AddParameter(command, "@schemaName", request.SchemaName);
        AddParameter(command, "@searchTerm", request.SearchTerm);
        AddParameter(command, "@maximumObjects", limits.MaximumRows + 1);

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
            limits,
            cancellationToken);
        var indexes = await ReadIndexesAsync(
            connection,
            transaction,
            descriptor.ObjectId,
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

    private static async Task<PostgreSqlObjectDescriptor> ReadObjectDescriptorAsync(
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
            SELECT candidate.oid,
                   candidate_namespace.nspname,
                   candidate.relname,
                   CASE candidate.relkind
                       WHEN 'r' THEN 'table'
                       WHEN 'p' THEN 'partitioned-table'
                       WHEN 'v' THEN 'view'
                       WHEN 'm' THEN 'materialized-view'
                       WHEN 'f' THEN 'foreign-table'
                       ELSE 'object'
                   END
            FROM pg_catalog.pg_class AS candidate
            INNER JOIN pg_catalog.pg_namespace AS candidate_namespace
                ON candidate_namespace.oid = candidate.relnamespace
            WHERE candidate.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND candidate_namespace.nspname = @schemaName
              AND candidate.relname = @objectName
              AND has_schema_privilege(current_user, candidate_namespace.oid, 'USAGE')
              AND (
                  has_table_privilege(current_user, candidate.oid, 'SELECT') OR
                  EXISTS (
                      SELECT 1
                      FROM pg_catalog.pg_attribute AS selectable_column
                      WHERE selectable_column.attrelid = candidate.oid
                        AND selectable_column.attnum > 0
                        AND NOT selectable_column.attisdropped
                        AND has_column_privilege(
                            current_user,
                            candidate.oid,
                            selectable_column.attname,
                            'SELECT')
                  )
              )
            LIMIT 1;
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

        return new PostgreSqlObjectDescriptor(
            reader.GetFieldValue<uint>(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }

    private static async Task<IReadOnlyList<DatabaseSchemaColumnResponse>> ReadColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        uint objectId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT candidate.attnum,
                   candidate.attname,
                   pg_catalog.format_type(candidate.atttypid, candidate.atttypmod),
                   NOT candidate.attnotnull,
                   EXISTS (
                       SELECT 1
                       FROM pg_catalog.pg_constraint AS primary_key
                       WHERE primary_key.conrelid = candidate.attrelid
                         AND primary_key.contype = 'p'
                         AND candidate.attnum = ANY(primary_key.conkey)
                   )
            FROM pg_catalog.pg_attribute AS candidate
            WHERE candidate.attrelid = @objectId
              AND candidate.attnum > 0
              AND NOT candidate.attisdropped
              AND has_column_privilege(current_user, candidate.attrelid, candidate.attname, 'SELECT')
            ORDER BY candidate.attnum;
            """;
        AddParameter(command, "@objectId", objectId);

        var columns = new List<DatabaseSchemaColumnResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new DatabaseSchemaColumnResponse
            {
                Ordinal = reader.GetInt16(0),
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
        uint objectId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT index_object.relname,
                   candidate.indisunique,
                   candidate.indisprimary,
                   candidate.indpred IS NOT NULL,
                   index_column.position,
                   indexed_column.attname,
                   index_column.position > candidate.indnkeyatts,
                   CASE
                       WHEN index_column.position > candidate.indnkeyatts THEN NULL
                       WHEN (candidate.indoption[index_column.position - 1] & 1) = 1 THEN 'descending'
                       ELSE 'ascending'
                   END
            FROM pg_catalog.pg_index AS candidate
            INNER JOIN pg_catalog.pg_class AS index_object
                ON index_object.oid = candidate.indexrelid
            CROSS JOIN LATERAL generate_series(1, candidate.indnatts)
                AS index_column(position)
            INNER JOIN pg_catalog.pg_attribute AS indexed_column
                ON indexed_column.attrelid = candidate.indexrelid
               AND indexed_column.attnum = index_column.position
            WHERE candidate.indrelid = @objectId
              AND candidate.indexprs IS NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM generate_series(1, candidate.indnatts)
                      AS denied_index_column(position)
                  INNER JOIN pg_catalog.pg_attribute AS denied_column
                      ON denied_column.attrelid = candidate.indexrelid
                     AND denied_column.attnum = denied_index_column.position
                  WHERE NOT has_column_privilege(
                        current_user,
                        candidate.indrelid,
                        denied_column.attname,
                        'SELECT')
              )
            ORDER BY candidate.indisprimary DESC, index_object.relname, index_column.position;
            """;
        AddParameter(command, "@objectId", objectId);

        var builders = new List<PostgreSqlIndexBuilder>();
        var byName = new Dictionary<string, PostgreSqlIndexBuilder>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!byName.TryGetValue(name, out var builder))
            {
                builder = new PostgreSqlIndexBuilder(
                    name,
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3));
                byName.Add(name, builder);
                builders.Add(builder);
            }

            var columnName = reader.GetString(5);
            if (reader.GetBoolean(6))
            {
                builder.IncludedColumns.Add(columnName);
            }
            else
            {
                builder.KeyColumns.Add(new DatabaseSchemaIndexColumnResponse
                {
                    Name = columnName,
                    Direction = reader.GetString(7)
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
        if (parameter is NpgsqlParameter npgsqlParameter && value is uint)
        {
            npgsqlParameter.NpgsqlDbType = NpgsqlDbType.Oid;
        }

        command.Parameters.Add(parameter);
    }

    private static string Quote(string schemaName, string objectName) =>
        $"\"{schemaName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"." +
        $"\"{objectName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static DatabaseReadProviderFailure Failure(
        string classification,
        string? providerCode,
        bool transient,
        string message) =>
        new("postgresql", classification, providerCode, transient, message);

    private sealed record PostgreSqlObjectDescriptor(
        uint ObjectId,
        string Schema,
        string Name,
        string Kind);

    private sealed class PostgreSqlIndexBuilder(
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
