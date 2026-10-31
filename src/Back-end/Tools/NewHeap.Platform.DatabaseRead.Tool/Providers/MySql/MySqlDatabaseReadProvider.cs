using MySqlConnector;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// The MySQL and MariaDB provider. Both flavors speak the same protocol through MySqlConnector, but
/// differ in their statement time limit, their roles and their privileges, so a profile names the
/// flavor, or <c>mysql-compatible</c> for either, and the provider checks the server's flavor and
/// minimum version from the version the server reported while the connection opened, before any
/// statement runs. Every flavor-dependent statement follows the flavor of its own connection.
/// Schema evidence comes from <c>information_schema</c>, which shows only what the principal holds a
/// privilege on.
/// </summary>
internal sealed partial class MySqlDatabaseReadProvider : IDatabaseReadProvider
{
    /// <summary>
    /// The oldest MySQL version whose roles, mandatory roles and dynamic privileges the
    /// verification reads.
    /// </summary>
    internal static readonly Version MinimumMySqlVersion = new(8, 0);

    /// <summary>
    /// The oldest MariaDB version the verification reads, the first with privileges granted to
    /// <c>PUBLIC</c>.
    /// </summary>
    internal static readonly Version MinimumMariaDbVersion = new(10, 11);

    /// <summary>
    /// <c>CURRENT_ROLE()</c> exists from MySQL 8.0, so it runs only after the server check.
    /// </summary>
    private const string CurrentRoleSql = "SELECT CURRENT_ROLE();";

    /// <summary>
    /// The roles granted directly to the MariaDB account. Roles granted to those roles are part of
    /// what <c>SHOW GRANTS</c> shows while their role is active.
    /// </summary>
    private const string MariaDbDirectRolesSql =
        """
        SELECT ROLE_NAME
        FROM information_schema.APPLICABLE_ROLES
        WHERE GRANTEE = CURRENT_USER()
        ORDER BY ROLE_NAME;
        """;

    /// <summary>
    /// A view with SQL SECURITY DEFINER runs with its definer's privileges, also the stored
    /// functions it calls, which then run without the principal needing EXECUTE. The principal sees
    /// a view here when it holds a privilege on it, which after the grant checks can only be
    /// SELECT; its own views run as itself.
    /// </summary>
    private const string DefinerViewSql =
        """
        SELECT COUNT(*)
        FROM information_schema.VIEWS
        WHERE SECURITY_TYPE = 'DEFINER' AND DEFINER <> CURRENT_USER();
        """;

    /// <summary>
    /// The profile's value: <see cref="DatabaseProviderKind.MySql"/> or
    /// <see cref="DatabaseProviderKind.MariaDb"/>, which accept only that flavor, or
    /// <see cref="DatabaseProviderKind.MySqlCompatible"/>, which accepts either.
    /// </summary>
    private readonly DatabaseProviderKind _profileProvider;

    /// <summary>
    /// The flavor of the last server that passed the check, which <see cref="Name"/> reports.
    /// </summary>
    private DatabaseProviderKind? _detectedFlavor;

    public MySqlDatabaseReadProvider(DatabaseProviderKind profileProvider)
    {
        if (profileProvider is not (DatabaseProviderKind.MySql
            or DatabaseProviderKind.MariaDb
            or DatabaseProviderKind.MySqlCompatible))
        {
            throw new ArgumentOutOfRangeException(nameof(profileProvider), profileProvider, null);
        }

        _profileProvider = profileProvider;
    }

    /// <summary>
    /// <c>mysql</c> or <c>mariadb</c>. A <c>mysql-compatible</c> profile reports the flavor of its
    /// server once a connection passed the check, and <c>mysql-compatible</c> before, such as for
    /// <c>validate</c> or a connection that could not be opened.
    /// </summary>
    public string Name => (_detectedFlavor ?? _profileProvider) switch
    {
        DatabaseProviderKind.MySql => "mysql",
        DatabaseProviderKind.MariaDb => "mariadb",
        _ => "mysql-compatible"
    };

    public DbConnection CreateConnection(
        string connectionString,
        string requestId,
        DatabaseReadLimits limits)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"NewHeap database read {requestId}",
            ConnectionTimeout = (uint)Math.Min(limits.TimeoutSeconds, 30),
            DefaultCommandTimeout = (uint)limits.TimeoutSeconds,
            AllowUserVariables = false,
            AllowLoadLocalInfile = false
        };

        return new MySqlConnection(builder.ConnectionString);
    }

    /// <summary>
    /// MySqlConnector resets a pooled connection with <c>COM_RESET_CONNECTION</c> when it comes
    /// back from the pool, which clears session variables, temporary tables and user variables.
    /// </summary>
    public string CreateReusableConnectionString(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            ConnectionReset = true
        };

        return builder.ConnectionString;
    }

    public string GetPhysicalConnectionId(DbConnection connection)
    {
        return ((MySqlConnection)connection).ServerThread.ToString(CultureInfo.InvariantCulture);
    }

    public Task ClearConnectionPoolAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        return MySqlConnection.ClearPoolAsync((MySqlConnection)connection, cancellationToken);
    }

    /// <summary>
    /// Checks the flavor and the minimum version from the version the server reported in its
    /// handshake, so a server older than MySQL 8.0, which does not know <c>CURRENT_ROLE()</c> or
    /// roles, stops with <c>provider-version-not-supported</c> before any statement runs instead of
    /// failing the first statement as a permission problem.
    /// </summary>
    public void EnsureSupportedServer(DbConnection connection)
    {
        FlavorOf(connection);
    }

    /// <summary>
    /// Verifies the server flavor and then the principal, denying by default. The verification
    /// reads <c>SHOW GRANTS</c> with every role the principal can use active: on MySQL all granted
    /// and mandatory roles at once (<c>SET ROLE ALL</c>), whose grants, also those of nested roles,
    /// the statement then shows; on MariaDB, which activates one role at a time, each role granted
    /// to the account in turn, and <c>SHOW GRANTS</c> also shows what is granted to <c>PUBLIC</c>.
    /// With the same roles active it looks for a selectable view that runs as another definer.
    /// The roles the session had are restored afterwards, so the diagnostic runs with the
    /// principal's own active roles.
    /// </summary>
    public async Task<DatabaseReadPrincipalVerification> VerifyReadOnlyPrincipalAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var flavor = FlavorOf(connection);
        var currentRole = await ReadCurrentRoleAsync(connection, limits, cancellationToken);

        await ExecuteAsync(connection, null, LockTimeoutSql(limits), limits, cancellationToken);

        var failedChecks = new List<string>();
        if (flavor == DatabaseProviderKind.MySql)
        {
            await ExecuteAsync(connection, null, "SET ROLE ALL;", limits, cancellationToken);
            await CollectFailedChecksAsync(connection, flavor, limits, failedChecks, cancellationToken);
            await ExecuteAsync(connection, null, RestoreMySqlRolesSql(currentRole), limits, cancellationToken);
        }
        else
        {
            var roles = await ReadMariaDbDirectRolesAsync(connection, limits, cancellationToken);
            if (roles.Count == 0)
            {
                await ExecuteAsync(connection, null, "SET ROLE NONE;", limits, cancellationToken);
                await CollectFailedChecksAsync(connection, flavor, limits, failedChecks, cancellationToken);
            }

            foreach (var role in roles)
            {
                await ExecuteAsync(connection, null, $"SET ROLE {QuoteIdentifier(role)};", limits, cancellationToken);
                await CollectFailedChecksAsync(connection, flavor, limits, failedChecks, cancellationToken);
            }

            var restoreSql = currentRole is null
                ? "SET ROLE NONE;"
                : $"SET ROLE {QuoteIdentifier(currentRole)};";
            await ExecuteAsync(connection, null, restoreSql, limits, cancellationToken);
        }

        var failedCheck = MySqlGrantPolicy.First(failedChecks);

        return failedCheck is null
            ? new DatabaseReadPrincipalVerification(true)
            : new DatabaseReadPrincipalVerification(false, failedCheck);
    }

    public async Task<DbTransaction> BeginReadOnlyTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var mySqlConnection = (MySqlConnection)connection;

        return await mySqlConnection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            isReadOnly: true,
            cancellationToken);
    }

    /// <summary>
    /// The transaction already started read only. MySQL ends a SELECT after
    /// <c>max_execution_time</c> milliseconds and MariaDB any statement after
    /// <c>max_statement_time</c> seconds; both wait for a row lock at most
    /// <c>innodb_lock_wait_timeout</c> and for a metadata lock at most <c>lock_wait_timeout</c>
    /// seconds, so the profile's lock timeout is rounded up to whole seconds.
    /// </summary>
    public async Task ConfigureReadOnlyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var statementLimit = FlavorOf(connection) == DatabaseProviderKind.MySql
            ? FormattableString.Invariant($"SESSION max_execution_time = {limits.TimeoutSeconds * 1000}")
            : FormattableString.Invariant($"SESSION max_statement_time = {limits.TimeoutSeconds}");
        var lockSeconds = LockTimeoutSeconds(limits);

        await ExecuteAsync(
            connection,
            transaction,
            FormattableString.Invariant(
                $"SET {statementLimit}, SESSION innodb_lock_wait_timeout = {lockSeconds}, SESSION lock_wait_timeout = {lockSeconds};"),
            limits,
            cancellationToken);
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
        if (DatabaseReadExceptionInspector.IsCertificateValidationFailure(exception))
        {
            return UntrustedCertificateFailure();
        }

        var mySqlException = DatabaseReadExceptionInspector.Find<MySqlException>(exception);
        if (mySqlException is not null)
        {
            return ClassifyErrorNumber(mySqlException.Number, exception, stage);
        }

        if (stage == DatabaseReadExecutionStage.ConnectionOpen
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

    internal DatabaseReadProviderFailure ClassifyErrorNumber(
        int number,
        Exception exception,
        DatabaseReadExecutionStage stage)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var providerCode = number.ToString(CultureInfo.InvariantCulture);

        return number switch
        {
            1146 => Failure("object-not-found", providerCode, false,
                "A referenced database object does not exist."),
            1054 => Failure("column-not-found", providerCode, false,
                "A referenced database column does not exist."),
            1044 when stage == DatabaseReadExecutionStage.ConnectionOpen => Failure(
                "database-not-found", providerCode, false,
                "The configured database does not exist or is unavailable to the principal."),
            1044 or 1142 or 1143 or 1227 or 1370 => Failure("permission-denied", providerCode, false,
                "The database principal is not permitted to read a referenced object or column."),
            1064 or 1149 => Failure("syntax-error", providerCode, false,
                "The database rejected the SQL syntax."),
            3024 or 1969 => Failure("statement-timeout", providerCode, false,
                "The database cancelled the statement after its configured execution boundary."),
            (int)MySqlErrorCode.CommandTimeoutExpired when stage == DatabaseReadExecutionStage.ConnectionOpen =>
                ConnectionFailure(providerCode),
            (int)MySqlErrorCode.CommandTimeoutExpired => Failure("statement-timeout", providerCode, false,
                "The database cancelled the statement after its configured execution boundary."),
            1205 => Failure("lock-timeout", providerCode, false,
                "The database could not acquire a required lock within the configured boundary."),
            1213 => Failure("deadlock", providerCode, true, "retry-operation",
                "The database selected the diagnostic statement as a deadlock victim."),
            1049 => Failure("database-not-found", providerCode, false,
                "The configured database does not exist or is unavailable to the principal."),
            1045 or 1130 or 1698 => Failure("authentication-failed", providerCode, false,
                "The database rejected the configured principal."),
            1040 or 1042 or 1043 or 1047 or 1053 or 1081 or 1158 or 1159 or 1160 or 1161 or
                2002 or 2003 or 2005 or 2006 or 2013 or 2055 => ConnectionFailure(providerCode),
            _ when stage == DatabaseReadExecutionStage.ConnectionOpen
                   || DatabaseReadExceptionInspector.IsConnectivityFailure(exception) =>
                ConnectionFailure(providerCode),
            _ => Failure("database-failure", providerCode, false,
                "The database rejected or could not complete the diagnostic operation.")
        };
    }

    /// <summary>
    /// The flavor follows from the server's version: MariaDB names itself in it, MySQL does not.
    /// MariaDB may put <c>5.5.5-</c> in front of its own version, such as
    /// <c>5.5.5-10.11.9-MariaDB</c>, for clients that expect MySQL. A <c>mysql</c> or
    /// <c>mariadb</c> profile never runs against the other flavor, because their time limits, roles
    /// and privileges differ; a <c>mysql-compatible</c> profile takes the flavor of the server. Each
    /// flavor keeps its own minimum version.
    /// </summary>
    internal DatabaseProviderKind SupportedFlavorOf(string version)
    {
        var flavor = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
            ? DatabaseProviderKind.MariaDb
            : DatabaseProviderKind.MySql;
        if (_profileProvider != DatabaseProviderKind.MySqlCompatible && flavor != _profileProvider)
        {
            throw new DatabaseReadExpectedException(
                "provider-flavor-mismatch",
                $"The database server is not {FlavorName(_profileProvider)}, which the profile names as its provider.",
                DatabaseReadExitCode.InvalidProfile);
        }

        var isMariaDb = flavor == DatabaseProviderKind.MariaDb;
        var match = (isMariaDb ? MariaDbVersionPrefix() : VersionPrefix()).Match(version);
        var minimum = isMariaDb ? MinimumMariaDbVersion : MinimumMySqlVersion;
        if (!match.Success ||
            new Version(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) < minimum)
        {
            throw new DatabaseReadExpectedException(
                "provider-version-not-supported",
                $"The tool reads the privileges of {FlavorName(flavor)} {minimum} or later.",
                DatabaseReadExitCode.InvalidProfile);
        }

        return flavor;
    }

    /// <summary>
    /// MySQL reports the active roles as <c>NONE</c> or as a list such as
    /// <c>`reader_role`@`%`,`auditor`@`%`</c>, which is also the syntax of <c>SET ROLE</c>. Any other
    /// value restores the account's default roles.
    /// </summary>
    internal static string RestoreMySqlRolesSql(string? currentRole)
    {
        if (currentRole is null || string.Equals(currentRole, "NONE", StringComparison.Ordinal))
        {
            return "SET ROLE NONE;";
        }

        return MySqlRoleList().IsMatch(currentRole)
            ? $"SET ROLE {currentRole};"
            : "SET ROLE DEFAULT;";
    }

    /// <summary>
    /// The flavor of the server the connection is open to, checked against the profile and the
    /// flavor's minimum version. It is read from the connection each time, so every statement
    /// follows the flavor of its own connection; it runs no statement.
    /// </summary>
    private DatabaseProviderKind FlavorOf(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var flavor = SupportedFlavorOf(connection.ServerVersion);
        _detectedFlavor = flavor;

        return flavor;
    }

    private static string FlavorName(DatabaseProviderKind flavor)
    {
        return flavor == DatabaseProviderKind.MySql ? "MySQL" : "MariaDB";
    }

    private static async Task CollectFailedChecksAsync(
        DbConnection connection,
        DatabaseProviderKind flavor,
        DatabaseReadLimits limits,
        List<string> failedChecks,
        CancellationToken cancellationToken)
    {
        await using (var grants = connection.CreateCommand())
        {
            grants.CommandText = "SHOW GRANTS;";
            grants.CommandTimeout = limits.TimeoutSeconds;
            await using var reader = await grants.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var failedCheck = MySqlGrantPolicy.Classify(ReadString(reader, 0), flavor);
                if (failedCheck is not null)
                {
                    failedChecks.Add(failedCheck);
                }
            }
        }

        await using var definerViews = connection.CreateCommand();
        definerViews.CommandText = DefinerViewSql;
        definerViews.CommandTimeout = limits.TimeoutSeconds;
        var count = Convert.ToInt64(await definerViews.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (count > 0)
        {
            failedChecks.Add(MySqlGrantPolicy.DefinerView);
        }
    }

    private static async Task<string?> ReadCurrentRoleAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CurrentRoleSql;
        command.CommandTimeout = limits.TimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || await reader.IsDBNullAsync(0, cancellationToken))
        {
            return null;
        }

        return ReadString(reader, 0);
    }

    private static async Task<IReadOnlyList<string>> ReadMariaDbDirectRolesAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        var roles = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = MariaDbDirectRolesSql;
        command.CommandTimeout = limits.TimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(ReadString(reader, 0));
        }

        return roles;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = limits.TimeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string LockTimeoutSql(DatabaseReadLimits limits)
    {
        var lockSeconds = LockTimeoutSeconds(limits);

        return FormattableString.Invariant(
            $"SET SESSION innodb_lock_wait_timeout = {lockSeconds}, SESSION lock_wait_timeout = {lockSeconds};");
    }

    private static int LockTimeoutSeconds(DatabaseReadLimits limits)
    {
        return Math.Max(1, (limits.LockTimeoutMilliseconds + 999) / 1000);
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
            SELECT candidate.TABLE_SCHEMA,
                   candidate.TABLE_NAME,
                   CASE candidate.TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END
            FROM information_schema.TABLES AS candidate
            WHERE candidate.TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED', 'VIEW')
              AND candidate.TABLE_SCHEMA NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
              AND EXISTS (
                  SELECT 1
                  FROM information_schema.COLUMNS AS selectable_column
                  WHERE selectable_column.TABLE_SCHEMA = candidate.TABLE_SCHEMA
                    AND selectable_column.TABLE_NAME = candidate.TABLE_NAME
              )
              AND (@schemaName IS NULL OR candidate.TABLE_SCHEMA = @schemaName)
              AND (@searchTerm IS NULL OR LOCATE(LOWER(@searchTerm), LOWER(candidate.TABLE_NAME)) > 0)
            ORDER BY candidate.TABLE_SCHEMA, candidate.TABLE_NAME
            LIMIT @maximumObjects;
            """;
        AddParameter(command, "@schemaName", request.SchemaName);
        AddParameter(command, "@searchTerm", request.SearchTerm);
        AddParameter(command, "@maximumObjects", limits.MaximumRows + 1);

        var objects = new List<DatabaseSchemaObjectSummaryResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schemaName = ReadString(reader, 0);
            var objectName = ReadString(reader, 1);
            objects.Add(new DatabaseSchemaObjectSummaryResponse
            {
                Schema = schemaName,
                Name = objectName,
                Kind = ReadString(reader, 2),
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

    private async Task<DatabaseSchemaResultResponse> DescribeSchemaAsync(
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
            descriptor,
            limits,
            cancellationToken);
        var indexes = await ReadIndexesAsync(
            connection,
            transaction,
            descriptor,
            columns,
            limits,
            cancellationToken);
        var relationships = await ReadRelationshipsAsync(
            connection,
            transaction,
            descriptor,
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

    private async Task<DatabaseSchemaResultResponse> ReadIndexesSchemaAsync(
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
            descriptor,
            limits,
            cancellationToken);
        var indexes = await ReadIndexesAsync(
            connection,
            transaction,
            descriptor,
            columns,
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

    private static async Task<MySqlObjectDescriptor> ReadObjectDescriptorAsync(
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
            SELECT candidate.TABLE_SCHEMA,
                   candidate.TABLE_NAME,
                   CASE candidate.TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END
            FROM information_schema.TABLES AS candidate
            WHERE candidate.TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED', 'VIEW')
              AND candidate.TABLE_SCHEMA NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
              AND candidate.TABLE_SCHEMA = @schemaName
              AND candidate.TABLE_NAME = @objectName
              AND EXISTS (
                  SELECT 1
                  FROM information_schema.COLUMNS AS selectable_column
                  WHERE selectable_column.TABLE_SCHEMA = candidate.TABLE_SCHEMA
                    AND selectable_column.TABLE_NAME = candidate.TABLE_NAME
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

        return new MySqlObjectDescriptor(
            ReadString(reader, 0),
            ReadString(reader, 1),
            ReadString(reader, 2));
    }

    /// <summary>
    /// <c>information_schema.COLUMNS</c> lists only the columns the principal holds a privilege on,
    /// so a column-level grant shows only its columns.
    /// </summary>
    private static async Task<IReadOnlyList<DatabaseSchemaColumnResponse>> ReadColumnsAsync(
        DbConnection connection,
        DbTransaction transaction,
        MySqlObjectDescriptor descriptor,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT candidate.ORDINAL_POSITION,
                   candidate.COLUMN_NAME,
                   candidate.COLUMN_TYPE,
                   candidate.IS_NULLABLE = 'YES',
                   EXISTS (
                       SELECT 1
                       FROM information_schema.STATISTICS AS primary_column
                       WHERE primary_column.TABLE_SCHEMA = candidate.TABLE_SCHEMA
                         AND primary_column.TABLE_NAME = candidate.TABLE_NAME
                         AND primary_column.INDEX_NAME = 'PRIMARY'
                         AND primary_column.COLUMN_NAME = candidate.COLUMN_NAME
                   )
            FROM information_schema.COLUMNS AS candidate
            WHERE candidate.TABLE_SCHEMA = @schemaName
              AND candidate.TABLE_NAME = @objectName
            ORDER BY candidate.ORDINAL_POSITION;
            """;
        AddParameter(command, "@schemaName", descriptor.Schema);
        AddParameter(command, "@objectName", descriptor.Name);

        var columns = new List<DatabaseSchemaColumnResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new DatabaseSchemaColumnResponse
            {
                Ordinal = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                Name = ReadString(reader, 1),
                ProviderType = ReadString(reader, 2),
                AllowsNull = ReadBoolean(reader, 3),
                IsPrimaryKey = ReadBoolean(reader, 4)
            });
        }

        return columns;
    }

    /// <summary>
    /// <c>information_schema.STATISTICS</c> lists every index of a table the principal holds a
    /// privilege on, also over columns it may not select, so an index is returned only when the
    /// principal can select each of its columns. A MySQL functional key part is returned with its
    /// expression only when every column the expression names is selectable; otherwise the index
    /// is left out. Invisible MySQL indexes and ignored MariaDB indexes, which the optimizer does
    /// not use, are left out too. Neither flavor has partial indexes or included columns.
    /// </summary>
    private async Task<IReadOnlyList<DatabaseSchemaIndexResponse>> ReadIndexesAsync(
        DbConnection connection,
        DbTransaction transaction,
        MySqlObjectDescriptor descriptor,
        IReadOnlyList<DatabaseSchemaColumnResponse> columns,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText = FlavorOf(connection) == DatabaseProviderKind.MySql
            ? """
              SELECT candidate.INDEX_NAME,
                     LOWER(candidate.INDEX_TYPE),
                     candidate.NON_UNIQUE = 0,
                     candidate.INDEX_NAME = 'PRIMARY',
                     candidate.SEQ_IN_INDEX,
                     candidate.COLUMN_NAME,
                     candidate.COLLATION,
                     candidate.EXPRESSION
              FROM information_schema.STATISTICS AS candidate
              WHERE candidate.TABLE_SCHEMA = @schemaName
                AND candidate.TABLE_NAME = @objectName
                AND candidate.IS_VISIBLE = 'YES'
              ORDER BY candidate.INDEX_NAME = 'PRIMARY' DESC, candidate.INDEX_NAME, candidate.SEQ_IN_INDEX;
              """
            : """
              SELECT candidate.INDEX_NAME,
                     LOWER(candidate.INDEX_TYPE),
                     candidate.NON_UNIQUE = 0,
                     candidate.INDEX_NAME = 'PRIMARY',
                     candidate.SEQ_IN_INDEX,
                     candidate.COLUMN_NAME,
                     candidate.COLLATION,
                     NULL
              FROM information_schema.STATISTICS AS candidate
              WHERE candidate.TABLE_SCHEMA = @schemaName
                AND candidate.TABLE_NAME = @objectName
                AND candidate.IGNORED = 'NO'
              ORDER BY candidate.INDEX_NAME = 'PRIMARY' DESC, candidate.INDEX_NAME, candidate.SEQ_IN_INDEX;
              """;
        AddParameter(command, "@schemaName", descriptor.Schema);
        AddParameter(command, "@objectName", descriptor.Name);

        var selectableColumns = columns
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builders = new List<MySqlIndexBuilder>();
        var byName = new Dictionary<string, MySqlIndexBuilder>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = ReadString(reader, 0);
            if (!byName.TryGetValue(name, out var builder))
            {
                builder = new MySqlIndexBuilder(
                    name,
                    ReadString(reader, 1),
                    ReadBoolean(reader, 2),
                    ReadBoolean(reader, 3));
                byName.Add(name, builder);
                builders.Add(builder);
            }

            var columnName = reader.IsDBNull(5) ? null : ReadString(reader, 5);
            var expression = reader.IsDBNull(7) ? null : ReadString(reader, 7);
            var direction = (reader.IsDBNull(6) ? null : ReadString(reader, 6)) switch
            {
                "A" => "ascending",
                "D" => "descending",
                _ => "unspecified"
            };

            if (columnName is not null)
            {
                builder.Hidden |= !selectableColumns.Contains(columnName);
            }
            else if (expression is null || !ExpressionColumnsAreSelectable(expression, selectableColumns))
            {
                builder.Hidden = true;
            }

            builder.KeyColumns.Add(new DatabaseSchemaIndexColumnResponse
            {
                Position = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                Kind = columnName is null ? "expression" : "column",
                Name = columnName,
                Expression = columnName is null ? expression : null,
                Direction = direction
            });
        }

        return builders
            .Where(builder => !builder.Hidden)
            .Select(builder => new DatabaseSchemaIndexResponse
            {
                Name = builder.Name,
                AccessMethod = builder.AccessMethod,
                IsUnique = builder.IsUnique,
                IsPrimaryKey = builder.IsPrimaryKey,
                IsPartial = false,
                Predicate = null,
                Columns = builder.KeyColumns
                    .Where(column => column.Kind == "column" && column.Name is not null)
                    .Select(column => column.Name!)
                    .ToArray(),
                KeyColumns = builder.KeyColumns,
                IncludedColumns = []
            })
            .ToArray();
    }

    /// <summary>
    /// MySQL writes the expression of a functional key part with every column name in backticks and
    /// string literals in single quotes, such as <c>lower(`name`)</c>. An expression whose text this
    /// does not read counts as naming a column the principal may not select.
    /// </summary>
    internal static bool ExpressionColumnsAreSelectable(string expression, IReadOnlySet<string> selectableColumns)
    {
        var index = 0;
        while (index < expression.Length)
        {
            var current = expression[index];
            if (current == '\'')
            {
                index++;
                while (index < expression.Length && expression[index] != '\'')
                {
                    index += expression[index] == '\\' ? 2 : 1;
                }

                if (index >= expression.Length)
                {
                    return false;
                }

                index++;
                continue;
            }

            if (current == '`')
            {
                var name = new StringBuilder();
                index++;
                while (index < expression.Length)
                {
                    if (expression[index] == '`')
                    {
                        if (index + 1 < expression.Length && expression[index + 1] == '`')
                        {
                            name.Append('`');
                            index += 2;
                            continue;
                        }

                        break;
                    }

                    name.Append(expression[index]);
                    index++;
                }

                if (index >= expression.Length || !selectableColumns.Contains(name.ToString()))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (current == '"')
            {
                return false;
            }

            index++;
        }

        return true;
    }

    /// <summary>
    /// The foreign keys from and to the object, each only when the principal can select every
    /// column of both sides; <c>information_schema.COLUMNS</c> lists only the columns it holds a
    /// privilege on. MySQL and MariaDB keep no record of whether existing rows were checked against
    /// a foreign key, which a load with <c>foreign_key_checks</c> off skips, so a relationship
    /// reports <c>isValidated</c> false.
    /// </summary>
    private static async Task<DatabaseSchemaRelationshipsResponse> ReadRelationshipsAsync(
        DbConnection connection,
        DbTransaction transaction,
        MySqlObjectDescriptor descriptor,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT candidate.CONSTRAINT_NAME,
                   candidate.TABLE_SCHEMA,
                   candidate.TABLE_NAME,
                   candidate.REFERENCED_TABLE_SCHEMA,
                   candidate.REFERENCED_TABLE_NAME,
                   candidate.ORDINAL_POSITION,
                   candidate.COLUMN_NAME,
                   candidate.REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE AS candidate
            WHERE candidate.REFERENCED_TABLE_NAME IS NOT NULL
              AND (
                  (candidate.TABLE_SCHEMA = @schemaName AND candidate.TABLE_NAME = @objectName) OR
                  (candidate.REFERENCED_TABLE_SCHEMA = @schemaName AND candidate.REFERENCED_TABLE_NAME = @objectName)
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM information_schema.KEY_COLUMN_USAGE AS denied_pair
                  WHERE denied_pair.CONSTRAINT_SCHEMA = candidate.CONSTRAINT_SCHEMA
                    AND denied_pair.CONSTRAINT_NAME = candidate.CONSTRAINT_NAME
                    AND denied_pair.TABLE_SCHEMA = candidate.TABLE_SCHEMA
                    AND denied_pair.TABLE_NAME = candidate.TABLE_NAME
                    AND denied_pair.REFERENCED_TABLE_NAME IS NOT NULL
                    AND (
                        NOT EXISTS (
                            SELECT 1
                            FROM information_schema.COLUMNS AS source_column
                            WHERE source_column.TABLE_SCHEMA = denied_pair.TABLE_SCHEMA
                              AND source_column.TABLE_NAME = denied_pair.TABLE_NAME
                              AND source_column.COLUMN_NAME = denied_pair.COLUMN_NAME
                        ) OR
                        NOT EXISTS (
                            SELECT 1
                            FROM information_schema.COLUMNS AS target_column
                            WHERE target_column.TABLE_SCHEMA = denied_pair.REFERENCED_TABLE_SCHEMA
                              AND target_column.TABLE_NAME = denied_pair.REFERENCED_TABLE_NAME
                              AND target_column.COLUMN_NAME = denied_pair.REFERENCED_COLUMN_NAME
                        )
                    )
              )
            ORDER BY candidate.TABLE_SCHEMA,
                     candidate.TABLE_NAME,
                     candidate.CONSTRAINT_NAME,
                     candidate.ORDINAL_POSITION;
            """;
        AddParameter(command, "@schemaName", descriptor.Schema);
        AddParameter(command, "@objectName", descriptor.Name);

        var builders = new List<MySqlRelationshipBuilder>();
        var byKey = new Dictionary<(string Schema, string Table, string Name), MySqlRelationshipBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = ReadString(reader, 0);
            var sourceSchema = ReadString(reader, 1);
            var sourceTable = ReadString(reader, 2);
            var key = (sourceSchema, sourceTable, name);
            if (!byKey.TryGetValue(key, out var builder))
            {
                var targetSchema = ReadString(reader, 3);
                var targetTable = ReadString(reader, 4);
                builder = new MySqlRelationshipBuilder(
                    name,
                    IsSameObject(descriptor, sourceSchema, sourceTable),
                    IsSameObject(descriptor, targetSchema, targetTable),
                    new DatabaseSchemaRelationshipObjectResponse
                    {
                        Schema = sourceSchema,
                        Name = sourceTable,
                        SqlIdentifier = Quote(sourceSchema, sourceTable)
                    },
                    new DatabaseSchemaRelationshipObjectResponse
                    {
                        Schema = targetSchema,
                        Name = targetTable,
                        SqlIdentifier = Quote(targetSchema, targetTable)
                    });
                byKey.Add(key, builder);
                builders.Add(builder);
            }

            builder.ColumnPairs.Add(new DatabaseSchemaRelationshipColumnPairResponse
            {
                Position = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                SourceColumn = ReadString(reader, 6),
                TargetColumn = ReadString(reader, 7)
            });
        }

        return new DatabaseSchemaRelationshipsResponse
        {
            Outgoing = builders
                .Where(builder => builder.IsOutgoing)
                .Select(builder => builder.Build())
                .ToArray(),
            Incoming = builders
                .Where(builder => builder.IsIncoming)
                .Select(builder => builder.Build())
                .ToArray()
        };
    }

    private static bool IsSameObject(MySqlObjectDescriptor descriptor, string schemaName, string objectName)
    {
        return string.Equals(descriptor.Schema, schemaName, StringComparison.Ordinal) &&
               string.Equals(descriptor.Name, objectName, StringComparison.Ordinal);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Reads text that the server may send as a binary string, as it does for some
    /// <c>information_schema</c> columns.
    /// </summary>
    private static string ReadString(DbDataReader reader, int ordinal)
    {
        return reader.GetValue(ordinal) switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private static bool ReadBoolean(DbDataReader reader, int ordinal)
    {
        return reader.GetValue(ordinal) switch
        {
            bool value => value,
            var value => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0
        };
    }

    private static string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    private static string Quote(string schemaName, string objectName) =>
        $"{QuoteIdentifier(schemaName)}.{QuoteIdentifier(objectName)}";

    private DatabaseReadProviderFailure ConnectionFailure(string? providerCode)
    {
        return Failure(
            "connection-failed",
            providerCode,
            true,
            "network-access-required",
            "The database connection could not be established or was interrupted.");
    }

    private DatabaseReadProviderFailure UntrustedCertificateFailure()
    {
        return Failure(
            "tls-certificate-untrusted",
            null,
            false,
            "certificate-authority-trust-required",
            "The client did not trust the database server's TLS certificate.");
    }

    private DatabaseReadProviderFailure Failure(
        string classification,
        string? providerCode,
        bool transient,
        string message)
    {
        return Failure(classification, providerCode, transient, null, message);
    }

    private DatabaseReadProviderFailure Failure(
        string classification,
        string? providerCode,
        bool transient,
        string? retryHint,
        string message)
    {
        return new DatabaseReadProviderFailure(
            Name,
            classification,
            providerCode,
            transient,
            retryHint,
            message);
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"^(?:5\.5\.5-)?(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MariaDbVersionPrefix();

    [GeneratedRegex(@"^`(?:[^`]|``)*`@`(?:[^`]|``)*`(?:,`(?:[^`]|``)*`@`(?:[^`]|``)*`)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MySqlRoleList();

    private sealed record MySqlObjectDescriptor(
        string Schema,
        string Name,
        string Kind);

    private sealed class MySqlIndexBuilder(
        string name,
        string accessMethod,
        bool isUnique,
        bool isPrimaryKey)
    {
        public string Name { get; } = name;

        public string AccessMethod { get; } = accessMethod;

        public bool IsUnique { get; } = isUnique;

        public bool IsPrimaryKey { get; } = isPrimaryKey;

        public bool Hidden { get; set; }

        public List<DatabaseSchemaIndexColumnResponse> KeyColumns { get; } = [];
    }

    private sealed class MySqlRelationshipBuilder(
        string name,
        bool isOutgoing,
        bool isIncoming,
        DatabaseSchemaRelationshipObjectResponse source,
        DatabaseSchemaRelationshipObjectResponse target)
    {
        public bool IsOutgoing { get; } = isOutgoing;

        public bool IsIncoming { get; } = isIncoming;

        public List<DatabaseSchemaRelationshipColumnPairResponse> ColumnPairs { get; } = [];

        public DatabaseSchemaRelationshipResponse Build() => new()
        {
            Name = name,
            IsValidated = false,
            Source = source,
            Target = target,
            ColumnPairs = ColumnPairs
        };
    }
}
