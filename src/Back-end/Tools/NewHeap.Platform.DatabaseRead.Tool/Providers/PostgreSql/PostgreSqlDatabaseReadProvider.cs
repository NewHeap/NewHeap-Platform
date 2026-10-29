using Npgsql;
using NpgsqlTypes;
using System.Data.Common;

namespace NewHeap.Platform.DatabaseRead;

internal sealed class PostgreSqlDatabaseReadProvider : IDatabaseReadProvider
{
    /// <summary>
    /// Returns <c>verified</c>, or the first check that the principal fails. The checks cover the
    /// connected role and every role it is a member of, also without inheritance, because a member
    /// can switch to such a role. They deny by default: apart from the privileges the server
    /// granted when it was initialized, the principal may hold SELECT on relations and columns, may
    /// be a member of <c>pg_read_all_data</c> but of no other predefined role, and may neither own
    /// objects nor execute functions outside <c>pg_catalog</c> and <c>information_schema</c>.
    /// Temporary schemas of other sessions are skipped; schema names are compared exactly, never
    /// as <c>LIKE</c> patterns.
    /// </summary>
    private const string ReadOnlyVerificationSql =
        """
        WITH principal_role AS (
            SELECT
                role.oid,
                role.rolname,
                role.rolsuper,
                role.rolcreaterole,
                role.rolcreatedb,
                role.rolreplication,
                role.rolbypassrls
            FROM pg_catalog.pg_roles AS role
            WHERE pg_catalog.pg_has_role(current_user, role.oid, 'MEMBER')
        ),
        principal_grantee AS (
            SELECT principal_role.oid
            FROM principal_role
            UNION ALL
            SELECT 0::pg_catalog.oid
        )
        SELECT CASE
            WHEN EXISTS (SELECT 1 FROM principal_role WHERE principal_role.rolsuper)
                THEN 'role-attribute-superuser'
            WHEN EXISTS (SELECT 1 FROM principal_role WHERE principal_role.rolcreaterole)
                THEN 'role-attribute-createrole'
            WHEN EXISTS (SELECT 1 FROM principal_role WHERE principal_role.rolcreatedb)
                THEN 'role-attribute-createdb'
            WHEN EXISTS (SELECT 1 FROM principal_role WHERE principal_role.rolreplication)
                THEN 'role-attribute-replication'
            WHEN EXISTS (SELECT 1 FROM principal_role WHERE principal_role.rolbypassrls)
                THEN 'role-attribute-bypassrls'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_database AS candidate
                WHERE candidate.datname = pg_catalog.current_database() AND
                      candidate.datdba IN (SELECT principal_role.oid FROM principal_role)
            ) THEN 'database-owner'
            WHEN EXISTS (
                SELECT 1
                FROM principal_role
                WHERE pg_catalog.starts_with(principal_role.rolname, 'pg_') AND
                      principal_role.rolname <> 'pg_read_all_data'
            ) THEN 'predefined-role-membership'
            WHEN EXISTS (
                SELECT 1
                FROM principal_role
                WHERE pg_catalog.has_database_privilege(
                    principal_role.oid,
                    pg_catalog.current_database(),
                    'CREATE')
            ) THEN 'database-create'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_namespace AS candidate
                CROSS JOIN principal_role
                WHERE NOT pg_catalog.pg_is_other_temp_schema(candidate.oid) AND
                      candidate.oid <> pg_catalog.pg_my_temp_schema() AND
                      pg_catalog.has_schema_privilege(principal_role.oid, candidate.oid, 'CREATE')
            ) THEN 'schema-create'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_namespace AS candidate
                WHERE candidate.nspowner IN (SELECT principal_role.oid FROM principal_role)
                UNION ALL
                SELECT 1
                FROM pg_catalog.pg_class AS candidate
                WHERE candidate.relowner IN (SELECT principal_role.oid FROM principal_role)
                UNION ALL
                SELECT 1
                FROM pg_catalog.pg_proc AS candidate
                WHERE candidate.proowner IN (SELECT principal_role.oid FROM principal_role)
                UNION ALL
                SELECT 1
                FROM pg_catalog.pg_type AS candidate
                WHERE candidate.typowner IN (SELECT principal_role.oid FROM principal_role)
            ) THEN 'object-owner'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS candidate
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                    COALESCE(candidate.relacl, pg_catalog.acldefault('r', candidate.relowner))) AS privilege
                WHERE candidate.relkind IN ('r', 'p', 'f', 'v', 'm') AND
                      privilege.privilege_type <> 'SELECT' AND
                      privilege.grantee IN (SELECT principal_grantee.oid FROM principal_grantee) AND
                      NOT EXISTS (
                          SELECT 1
                          FROM pg_catalog.pg_init_privs AS initial
                          CROSS JOIN LATERAL pg_catalog.aclexplode(initial.initprivs) AS initial_privilege
                          WHERE initial.objoid = candidate.oid AND
                                initial.classoid = 'pg_catalog.pg_class'::pg_catalog.regclass AND
                                initial.objsubid = 0 AND
                                initial.privtype = 'i' AND
                                initial_privilege.grantee = privilege.grantee AND
                                initial_privilege.privilege_type = privilege.privilege_type
                      )
            ) THEN 'relation-write'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_attribute AS candidate
                CROSS JOIN LATERAL pg_catalog.aclexplode(candidate.attacl) AS privilege
                WHERE candidate.attacl IS NOT NULL AND
                      privilege.privilege_type <> 'SELECT' AND
                      privilege.grantee IN (SELECT principal_grantee.oid FROM principal_grantee) AND
                      NOT EXISTS (
                          SELECT 1
                          FROM pg_catalog.pg_init_privs AS initial
                          CROSS JOIN LATERAL pg_catalog.aclexplode(initial.initprivs) AS initial_privilege
                          WHERE initial.objoid = candidate.attrelid AND
                                initial.classoid = 'pg_catalog.pg_class'::pg_catalog.regclass AND
                                initial.objsubid = candidate.attnum AND
                                initial.privtype = 'i' AND
                                initial_privilege.grantee = privilege.grantee AND
                                initial_privilege.privilege_type = privilege.privilege_type
                      )
            ) THEN 'column-write'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS candidate
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                    COALESCE(candidate.relacl, pg_catalog.acldefault('s', candidate.relowner))) AS privilege
                WHERE candidate.relkind = 'S' AND
                      privilege.privilege_type <> 'SELECT' AND
                      privilege.grantee IN (SELECT principal_grantee.oid FROM principal_grantee)
            ) THEN 'sequence-usage'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_proc AS candidate
                INNER JOIN pg_catalog.pg_namespace AS candidate_namespace
                    ON candidate_namespace.oid = candidate.pronamespace
                CROSS JOIN principal_role
                WHERE candidate_namespace.nspname NOT IN ('pg_catalog', 'information_schema') AND
                      NOT pg_catalog.pg_is_other_temp_schema(candidate_namespace.oid) AND
                      pg_catalog.has_function_privilege(principal_role.oid, candidate.oid, 'EXECUTE')
            ) THEN 'function-execute'
            WHEN EXISTS (
                SELECT 1
                FROM pg_catalog.pg_proc AS candidate
                INNER JOIN pg_catalog.pg_init_privs AS initial
                    ON initial.objoid = candidate.oid AND
                       initial.classoid = 'pg_catalog.pg_proc'::pg_catalog.regclass AND
                       initial.objsubid = 0
                CROSS JOIN principal_role
                WHERE NOT EXISTS (
                          SELECT 1
                          FROM pg_catalog.aclexplode(initial.initprivs) AS initial_privilege
                          WHERE initial_privilege.grantee = 0 AND
                                initial_privilege.privilege_type = 'EXECUTE'
                      ) AND
                      pg_catalog.has_function_privilege(principal_role.oid, candidate.oid, 'EXECUTE')
            ) THEN 'catalog-function-execute'
            ELSE 'verified'
        END;
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

    /// <summary>
    /// The PostgreSQL verification has no minimum server version of its own.
    /// </summary>
    public void EnsureSupportedServer(DbConnection connection)
    {
    }

    public async Task<DatabaseReadPrincipalVerification> VerifyReadOnlyPrincipalAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ReadOnlyVerificationSql;
        command.CommandTimeout = limits.TimeoutSeconds;
        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is string check
            ? new DatabaseReadPrincipalVerification(check == "verified", check == "verified" ? null : check)
            : new DatabaseReadPrincipalVerification(false, "verification-result-invalid");
    }

    public Task<DbTransaction> BeginReadOnlyTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        return DatabaseReadTransactions.BeginReadCommittedAsync(connection, cancellationToken);
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

        // standard_conforming_strings keeps a backslash in a string literal an ordinary character,
        // as SqlReadOnlyPolicy reads it, also for a role or connection that turned it off.
        await using var timeoutCommand = connection.CreateCommand();
        timeoutCommand.Transaction = transaction;
        timeoutCommand.CommandText =
            "SELECT set_config('statement_timeout', @statementTimeout, true), " +
            "set_config('lock_timeout', @lockTimeout, true), " +
            "set_config('standard_conforming_strings', 'on', true);";
        timeoutCommand.CommandTimeout = limits.TimeoutSeconds;
        AddTextParameter(timeoutCommand, "@statementTimeout", $"{limits.TimeoutSeconds * 1000}ms");
        AddTextParameter(timeoutCommand, "@lockTimeout", $"{limits.LockTimeoutMilliseconds}ms");

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

    public DatabaseReadProviderFailure ClassifyException(
        DbException exception,
        DatabaseReadExecutionStage stage)
    {
        var postgresException = DatabaseReadExceptionInspector.Find<PostgresException>(exception);
        if (postgresException is not null)
        {
            return ClassifySqlState(postgresException.SqlState, exception, stage);
        }

        if (DatabaseReadExceptionInspector.IsCertificateValidationFailure(exception))
        {
            return UntrustedCertificateFailure();
        }

        if (stage == DatabaseReadExecutionStage.ConnectionOpen
            || DatabaseReadExceptionInspector.Find<NpgsqlException>(exception) is not null
            || DatabaseReadExceptionInspector.IsConnectivityFailure(exception))
        {
            return ConnectionFailure(null);
        }

        return Failure(
            "database-failure",
            null,
            false,
            "The database rejected or could not complete the diagnostic operation.");
    }

    internal static DatabaseReadProviderFailure ClassifySqlState(
        string sqlState,
        Exception exception,
        DatabaseReadExecutionStage stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlState);
        ArgumentNullException.ThrowIfNull(exception);

        var providerCode = GetSafeSqlState(sqlState);

        return sqlState switch
        {
            "42P01" => Failure("object-not-found", providerCode, false,
                "A referenced database object does not exist."),
            "42703" => Failure("column-not-found", providerCode, false,
                "A referenced database column does not exist."),
            "42501" => Failure("permission-denied", providerCode, false,
                "The database principal is not permitted to read a referenced object or column."),
            "42601" => Failure("syntax-error", providerCode, false,
                "The database rejected the SQL syntax."),
            "57014" => Failure("statement-timeout", providerCode, false,
                "The database cancelled the statement after its configured execution boundary."),
            "55P03" => Failure("lock-timeout", providerCode, false,
                "The database could not acquire a required lock within the configured boundary."),
            "40P01" => Failure("deadlock", providerCode, true, "retry-operation",
                "The database selected the diagnostic statement as a deadlock victim."),
            "3D000" => Failure("database-not-found", providerCode, false,
                "The configured database does not exist or is unavailable to the principal."),
            "3F000" => Failure("schema-not-found", providerCode, false,
                "A referenced database schema does not exist."),
            "28000" or "28P01" => Failure("authentication-failed", providerCode, false,
                "The database rejected the configured principal."),
            _ when sqlState.StartsWith("08", StringComparison.Ordinal) =>
                ConnectionFailure(providerCode),
            _ when stage == DatabaseReadExecutionStage.ConnectionOpen
                   || DatabaseReadExceptionInspector.IsConnectivityFailure(exception) =>
                ConnectionFailure(providerCode),
            _ => Failure("database-failure", providerCode, false,
                "The database rejected or could not complete the diagnostic operation.")
        };
    }

    private static string? GetSafeSqlState(string sqlState)
    {
        if (sqlState.Length != 5)
        {
            return null;
        }

        return sqlState.All(character => character is >= '0' and <= '9'
            or >= 'A' and <= 'Z')
            ? sqlState
            : null;
    }

    private static DatabaseReadProviderFailure ConnectionFailure(string? providerCode)
    {
        return Failure(
            "connection-failed",
            providerCode,
            true,
            "network-access-required",
            "The database connection could not be established or was interrupted.");
    }

    /// <summary>
    /// Npgsql reports a server certificate that failed validation as an
    /// <see cref="NpgsqlException"/> around the handshake's <c>AuthenticationException</c>. It
    /// carries no SQLSTATE and does not say which check failed, so an untrusted authority, a host
    /// name the certificate does not name and an expired certificate report the same failure.
    /// </summary>
    private static DatabaseReadProviderFailure UntrustedCertificateFailure()
    {
        return Failure(
            "tls-certificate-untrusted",
            null,
            false,
            "certificate-authority-trust-required",
            "The client did not trust the database server's TLS certificate.");
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
        AddTextParameter(command, "@schemaName", request.SchemaName);
        AddTextParameter(command, "@searchTerm", request.SearchTerm);
        AddIntegerParameter(command, "@maximumObjects", limits.MaximumRows + 1);

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
        var relationships = await ReadRelationshipsAsync(
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
                Indexes = indexes,
                Relationships = relationships
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
        AddTextParameter(command, "@schemaName", schemaName);
        AddTextParameter(command, "@objectName", objectName);

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
        AddOidParameter(command, "@objectId", objectId);

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
                   access_method.amname,
                   candidate.indisunique,
                   candidate.indisprimary,
                   candidate.indpred IS NOT NULL,
                   CASE
                       WHEN has_table_privilege(current_user, candidate.indrelid, 'SELECT')
                       THEN pg_catalog.pg_get_expr(candidate.indpred, candidate.indrelid, false)
                       ELSE NULL
                   END,
                   index_column.position,
                   index_column.position > candidate.indnkeyatts,
                   CASE
                       WHEN index_column.position > candidate.indnkeyatts THEN NULL
                       WHEN access_method.amname <> 'btree' THEN 'unspecified'
                       WHEN (candidate.indoption[index_column.position - 1] & 1) = 1 THEN 'descending'
                       ELSE 'ascending'
                   END,
                   indexed_column.attname,
                   CASE
                       WHEN candidate.indkey[index_column.position - 1] = 0
                            AND has_table_privilege(current_user, candidate.indrelid, 'SELECT')
                       THEN pg_catalog.pg_get_indexdef(
                           candidate.indexrelid,
                           index_column.position,
                           false)
                       ELSE NULL
                   END
            FROM pg_catalog.pg_index AS candidate
            INNER JOIN pg_catalog.pg_class AS index_object
                ON index_object.oid = candidate.indexrelid
            INNER JOIN pg_catalog.pg_am AS access_method
                ON access_method.oid = index_object.relam
            CROSS JOIN LATERAL generate_series(1, candidate.indnatts)
                AS index_column(position)
            LEFT JOIN pg_catalog.pg_attribute AS indexed_column
                ON indexed_column.attrelid = candidate.indrelid
               AND indexed_column.attnum = candidate.indkey[index_column.position - 1]
            WHERE candidate.indrelid = @objectId
              AND candidate.indisvalid
              AND candidate.indisready
              AND candidate.indislive
              AND (
                  has_table_privilege(current_user, candidate.indrelid, 'SELECT') OR
                  (
                      candidate.indexprs IS NULL AND
                      NOT EXISTS (
                          SELECT 1
                          FROM generate_series(1, candidate.indnatts)
                              AS denied_index_column(position)
                          LEFT JOIN pg_catalog.pg_attribute AS denied_column
                              ON denied_column.attrelid = candidate.indrelid
                             AND denied_column.attnum =
                                 candidate.indkey[denied_index_column.position - 1]
                          WHERE denied_column.attname IS NULL OR
                                NOT has_column_privilege(
                                    current_user,
                                    candidate.indrelid,
                                    denied_column.attname,
                                    'SELECT')
                      )
                  )
              )
            ORDER BY candidate.indisprimary DESC, index_object.relname, index_column.position;
            """;
        AddOidParameter(command, "@objectId", objectId);

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
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
                byName.Add(name, builder);
                builders.Add(builder);
            }

            var position = reader.GetInt32(6);
            var columnName = reader.IsDBNull(9) ? null : reader.GetString(9);
            if (reader.GetBoolean(7))
            {
                if (columnName is not null)
                {
                    builder.IncludedColumns.Add(columnName);
                }
            }
            else
            {
                var expression = reader.IsDBNull(10) ? null : reader.GetString(10);
                builder.KeyColumns.Add(new DatabaseSchemaIndexColumnResponse
                {
                    Position = position,
                    Kind = expression is null ? "column" : "expression",
                    Name = columnName,
                    Expression = expression,
                    Direction = reader.GetString(8)
                });
            }
        }

        return builders
            .Select(builder => new DatabaseSchemaIndexResponse
            {
                Name = builder.Name,
                AccessMethod = builder.AccessMethod,
                IsUnique = builder.IsUnique,
                IsPrimaryKey = builder.IsPrimaryKey,
                IsPartial = builder.IsPartial,
                Predicate = builder.Predicate,
                Columns = builder.KeyColumns
                    .Where(column => column.Name is not null)
                    .Select(column => column.Name!)
                    .ToArray(),
                KeyColumns = builder.KeyColumns,
                IncludedColumns = builder.IncludedColumns
            })
            .ToArray();
    }

    private static async Task<DatabaseSchemaRelationshipsResponse> ReadRelationshipsAsync(
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
            SELECT candidate.oid,
                   candidate.conname,
                   candidate.convalidated,
                   source_object.oid,
                   source_schema.nspname,
                   source_object.relname,
                   target_object.oid,
                   target_schema.nspname,
                   target_object.relname,
                   source_key.position,
                   source_column.attname,
                   target_column.attname
            FROM pg_catalog.pg_constraint AS candidate
            INNER JOIN pg_catalog.pg_class AS source_object
                ON source_object.oid = candidate.conrelid
            INNER JOIN pg_catalog.pg_namespace AS source_schema
                ON source_schema.oid = source_object.relnamespace
            INNER JOIN pg_catalog.pg_class AS target_object
                ON target_object.oid = candidate.confrelid
            INNER JOIN pg_catalog.pg_namespace AS target_schema
                ON target_schema.oid = target_object.relnamespace
            CROSS JOIN LATERAL unnest(candidate.conkey) WITH ORDINALITY
                AS source_key(attribute_number, position)
            INNER JOIN LATERAL unnest(candidate.confkey) WITH ORDINALITY
                AS target_key(attribute_number, position)
                ON target_key.position = source_key.position
            INNER JOIN pg_catalog.pg_attribute AS source_column
                ON source_column.attrelid = source_object.oid
               AND source_column.attnum = source_key.attribute_number
            INNER JOIN pg_catalog.pg_attribute AS target_column
                ON target_column.attrelid = target_object.oid
               AND target_column.attnum = target_key.attribute_number
            WHERE candidate.contype = 'f'
              AND (candidate.conrelid = @objectId OR candidate.confrelid = @objectId)
              AND has_schema_privilege(current_user, source_schema.oid, 'USAGE')
              AND has_schema_privilege(current_user, target_schema.oid, 'USAGE')
              AND NOT EXISTS (
                  SELECT 1
                  FROM unnest(candidate.conkey) WITH ORDINALITY
                      AS denied_source_key(attribute_number, position)
                  INNER JOIN LATERAL unnest(candidate.confkey) WITH ORDINALITY
                      AS denied_target_key(attribute_number, position)
                      ON denied_target_key.position = denied_source_key.position
                  INNER JOIN pg_catalog.pg_attribute AS denied_source_column
                      ON denied_source_column.attrelid = source_object.oid
                     AND denied_source_column.attnum = denied_source_key.attribute_number
                  INNER JOIN pg_catalog.pg_attribute AS denied_target_column
                      ON denied_target_column.attrelid = target_object.oid
                     AND denied_target_column.attnum = denied_target_key.attribute_number
                  WHERE NOT has_column_privilege(
                            current_user,
                            source_object.oid,
                            denied_source_column.attname,
                            'SELECT') OR
                        NOT has_column_privilege(
                            current_user,
                            target_object.oid,
                            denied_target_column.attname,
                            'SELECT')
              )
            ORDER BY candidate.oid, source_key.position;
            """;
        AddOidParameter(command, "@objectId", objectId);

        var builders = new List<PostgreSqlRelationshipBuilder>();
        var byId = new Dictionary<uint, PostgreSqlRelationshipBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var relationshipId = reader.GetFieldValue<uint>(0);
            if (!byId.TryGetValue(relationshipId, out var builder))
            {
                builder = new PostgreSqlRelationshipBuilder(
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetFieldValue<uint>(3),
                    new DatabaseSchemaRelationshipObjectResponse
                    {
                        Schema = reader.GetString(4),
                        Name = reader.GetString(5),
                        SqlIdentifier = Quote(reader.GetString(4), reader.GetString(5))
                    },
                    reader.GetFieldValue<uint>(6),
                    new DatabaseSchemaRelationshipObjectResponse
                    {
                        Schema = reader.GetString(7),
                        Name = reader.GetString(8),
                        SqlIdentifier = Quote(reader.GetString(7), reader.GetString(8))
                    });
                byId.Add(relationshipId, builder);
                builders.Add(builder);
            }

            builder.ColumnPairs.Add(new DatabaseSchemaRelationshipColumnPairResponse
            {
                Position = checked((int)reader.GetInt64(9)),
                SourceColumn = reader.GetString(10),
                TargetColumn = reader.GetString(11)
            });
        }

        return new DatabaseSchemaRelationshipsResponse
        {
            Outgoing = builders
                .Where(builder => builder.SourceObjectId == objectId)
                .Select(builder => builder.Build())
                .ToArray(),
            Incoming = builders
                .Where(builder => builder.TargetObjectId == objectId)
                .Select(builder => builder.Build())
                .ToArray()
        };
    }

    /// <summary>
    /// Binds a string that may be omitted. PostgreSQL cannot infer the type of an untyped null in
    /// <c>@value IS NULL</c> and rejects the statement with SQLSTATE 42P08, so every text parameter
    /// carries its type, with or without a value.
    /// </summary>
    private static void AddTextParameter(DbCommand command, string name, string? value)
    {
        AddParameter(command, name, NpgsqlDbType.Text, value);
    }

    private static void AddIntegerParameter(DbCommand command, string name, int value)
    {
        AddParameter(command, name, NpgsqlDbType.Integer, value);
    }

    private static void AddOidParameter(DbCommand command, string name, uint value)
    {
        AddParameter(command, name, NpgsqlDbType.Oid, value);
    }

    private static void AddParameter(DbCommand command, string name, NpgsqlDbType type, object? value)
    {
        var parameter = new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        };

        command.Parameters.Add(parameter);
    }

    private static string Quote(string schemaName, string objectName) =>
        $"\"{schemaName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"." +
        $"\"{objectName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static DatabaseReadProviderFailure Failure(
        string classification,
        string? providerCode,
        bool transient,
        string message)
    {
        return Failure(classification, providerCode, transient, null, message);
    }

    private static DatabaseReadProviderFailure Failure(
        string classification,
        string? providerCode,
        bool transient,
        string? retryHint,
        string message)
    {
        return new DatabaseReadProviderFailure(
            "postgresql",
            classification,
            providerCode,
            transient,
            retryHint,
            message);
    }

    private sealed record PostgreSqlObjectDescriptor(
        uint ObjectId,
        string Schema,
        string Name,
        string Kind);

    private sealed class PostgreSqlIndexBuilder(
        string name,
        string accessMethod,
        bool isUnique,
        bool isPrimaryKey,
        bool isPartial,
        string? predicate)
    {
        public string Name { get; } = name;

        public string AccessMethod { get; } = accessMethod;

        public bool IsUnique { get; } = isUnique;

        public bool IsPrimaryKey { get; } = isPrimaryKey;

        public bool IsPartial { get; } = isPartial;

        public string? Predicate { get; } = predicate;

        public List<DatabaseSchemaIndexColumnResponse> KeyColumns { get; } = [];

        public List<string> IncludedColumns { get; } = [];
    }

    private sealed class PostgreSqlRelationshipBuilder(
        string name,
        bool isValidated,
        uint sourceObjectId,
        DatabaseSchemaRelationshipObjectResponse source,
        uint targetObjectId,
        DatabaseSchemaRelationshipObjectResponse target)
    {
        public string Name { get; } = name;

        public bool IsValidated { get; } = isValidated;

        public uint SourceObjectId { get; } = sourceObjectId;

        public uint TargetObjectId { get; } = targetObjectId;

        public List<DatabaseSchemaRelationshipColumnPairResponse> ColumnPairs { get; } = [];

        public DatabaseSchemaRelationshipResponse Build() => new()
        {
            Name = Name,
            IsValidated = IsValidated,
            Source = source,
            Target = target,
            ColumnPairs = ColumnPairs
        };
    }
}