using Microsoft.Data.SqlClient;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace NewHeap.Platform.DatabaseRead;

internal sealed class SqlServerDatabaseReadProvider : IDatabaseReadProvider
{
    /// <summary>
    /// <c>SEC_E_UNTRUSTED_ROOT</c>: the certificate chain was issued by an authority that is not
    /// trusted. SqlClient reports it on both its native and its managed network layer.
    /// </summary>
    private const int SecurityUntrustedRoot = unchecked((int)0x80090325);

    /// <summary>
    /// <c>CERT_E_UNTRUSTEDROOT</c>: the chain ends in a root certificate that is not trusted.
    /// </summary>
    private const int CertificateUntrustedRoot = unchecked((int)0x800B0109);

    /// <summary>
    /// <c>CERT_E_CHAINING</c>: no chain could be built to a trusted root certificate.
    /// </summary>
    private const int CertificateChaining = unchecked((int)0x800B010A);

    /// <summary>
    /// SQL Server error 1205: the server chose the statement as deadlock victim.
    /// </summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// SQL Server error 1222: a lock was not granted within <c>LOCK_TIMEOUT</c>.
    /// </summary>
    private const int LockRequestTimeout = 1222;

    /// <summary>
    /// How often the principal verification runs at most when it meets a lock conflict.
    /// </summary>
    internal const int MaximumVerificationAttempts = 8;

    /// <summary>
    /// A retry starts only when at least this much of the request's timeout is left for it.
    /// </summary>
    private static readonly TimeSpan MinimumVerificationAttemptTime = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Returns <c>verified</c>, or the first check that the principal fails. The named checks come
    /// first and keep their order, so a principal reports the same check as before; the
    /// <c>*-not-allowlisted</c> checks after them deny by default. They test the effective server,
    /// database, schema and object permissions of every permission name the server knows, which
    /// includes permissions held through roles, ownership and implication, and allow only
    /// connecting, SELECT, metadata visibility (<c>VIEW DEFINITION</c>, <c>VIEW CHANGE TRACKING</c>,
    /// <c>SHOWPLAN</c>) and the column-key definitions that every database grants to public. Schemas
    /// owned by fixed database roles are skipped, because a member of such a role controls that
    /// role's empty schema; objects in them are still checked.
    /// </summary>
    /// <remarks>
    /// Permissions on logins, server roles, users and roles come from their grants and owners in
    /// the catalog, not from <c>HAS_PERMS_BY_NAME</c> per principal: that took a metadata lock on
    /// each principal while the scan still held its catalog row, and deadlocked with a concurrent
    /// <c>CREATE LOGIN</c>, <c>GRANT</c> or <c>DROP LOGIN</c>, which takes them in the opposite
    /// order. What a principal holds through the server or the database, such as
    /// <c>IMPERSONATE ANY LOGIN</c> or <c>ALTER ANY ROLE</c>, is rejected by the server and
    /// database checks.
    /// </remarks>
    private const string VerificationBeforeFunctionSelectSql =
        """
        SELECT CASE
            WHEN COALESCE(IS_SRVROLEMEMBER('sysadmin'), 0) = 1 THEN 'server-role-sysadmin'
            WHEN COALESCE(IS_MEMBER('db_owner'), 0) = 1 THEN 'database-role-db_owner'
            WHEN COALESCE(IS_MEMBER('db_accessadmin'), 0) = 1 THEN 'database-role-db_accessadmin'
            WHEN COALESCE(IS_MEMBER('db_securityadmin'), 0) = 1 THEN 'database-role-db_securityadmin'
            WHEN COALESCE(IS_MEMBER('db_backupoperator'), 0) = 1 THEN 'database-role-db_backupoperator'
            WHEN COALESCE(IS_MEMBER('db_datawriter'), 0) = 1 THEN 'database-role-db_datawriter'
            WHEN COALESCE(IS_MEMBER('db_ddladmin'), 0) = 1 THEN 'database-role-db_ddladmin'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL SERVER') = 1 THEN 'server-permission-control-server'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY LOGIN') = 1 THEN 'server-permission-alter-any-login'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY DATABASE') = 1 THEN 'server-permission-alter-any-database'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE') = 1 THEN 'server-permission-create-any-database'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'ADMINISTER BULK OPERATIONS') = 1 THEN 'server-permission-administer-bulk-operations'
            WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CONTROL') = 1 THEN 'database-permission-control'
            WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') = 1 THEN 'database-permission-alter'
            WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE') = 1 THEN 'database-permission-create-table'
            WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE PROCEDURE') = 1 THEN 'database-permission-create-procedure'
            WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'EXECUTE') = 1 THEN 'database-permission-execute'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('U', 'V') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'INSERT') = 1
            ) THEN 'table-view-insert'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('U', 'V') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'UPDATE') = 1
            ) THEN 'table-view-update'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('U', 'V') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'DELETE') = 1
            ) THEN 'table-view-delete'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('P', 'PC') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'EXECUTE') = 1
            ) THEN 'procedure-execute'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('FN', 'IF', 'TF', 'FS', 'FT') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'EXECUTE') = 1
            ) THEN 'function-execute'
        """;

    /// <summary>
    /// The strict function policy: SELECT on any user-defined function rejects the principal,
    /// because the tool does not verify what a function depends on.
    /// </summary>
    private const string StrictFunctionSelectSql =
        """
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('FN', 'IF', 'TF', 'FS', 'FT') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'SELECT') = 1
            ) THEN 'function-select'
        """;

    /// <summary>
    /// The <c>inline-table-valued-select</c> policy at the place of <c>function-select</c>: SELECT on
    /// a function that is not an inline table-valued function still rejects the principal, and an
    /// inline one must be schema-bound and run without <c>EXECUTE AS</c>. SQL Server refuses
    /// <c>EXECUTE AS</c> on an inline function; the check keeps that boundary should it ever
    /// change. What such a function depends on is checked after all other checks, by
    /// <see cref="InlineFunctionDependencySql"/>.
    /// </summary>
    private const string InlineTableValuedFunctionSelectSql =
        """
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN ('FN', 'TF', 'FS', 'FT') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'SELECT') = 1
            ) THEN 'function-select'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                LEFT JOIN sys.sql_modules AS candidate_module
                    ON candidate_module.object_id = candidate.object_id
                WHERE candidate.type = 'IF' AND
                    candidate.is_ms_shipped = 0 AND
                    COALESCE(candidate_module.is_schema_bound, 0) = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'SELECT') = 1
            ) THEN 'inline-function-not-schema-bound'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                INNER JOIN sys.sql_modules AS candidate_module
                    ON candidate_module.object_id = candidate.object_id
                WHERE candidate.type = 'IF' AND
                    candidate.is_ms_shipped = 0 AND
                    candidate_module.execute_as_principal_id IS NOT NULL AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'SELECT') = 1
            ) THEN 'inline-function-execute-as'
        """;

    private const string VerificationAfterFunctionSelectSql =
        """
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1 THEN 'server-permission-view-server-state'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY CONNECTION') = 1 THEN 'server-permission-alter-any-connection'
            WHEN HAS_PERMS_BY_NAME(NULL, NULL, 'IMPERSONATE ANY LOGIN') = 1 THEN 'server-permission-impersonate-any-login'
            WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER ANY SCHEMA') = 1 THEN 'database-permission-alter-any-schema'
            WHEN EXISTS (
                SELECT 1
                FROM sys.server_permissions AS candidate
                WHERE candidate.class = 101 AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name = 'IMPERSONATE' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.login_token AS token)
            ) THEN 'login-impersonate'
            WHEN EXISTS (
                SELECT 1
                FROM sys.database_permissions AS candidate
                WHERE candidate.class = 4 AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name = 'IMPERSONATE' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.user_token AS token)
            ) THEN 'user-impersonate'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type = 'SO' AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'UPDATE') = 1
            ) THEN 'sequence-update'
            WHEN EXISTS (
                SELECT 1
                FROM sys.schemas AS candidate
                LEFT JOIN sys.database_principals AS candidate_owner
                    ON candidate_owner.principal_id = candidate.principal_id
                WHERE COALESCE(candidate_owner.is_fixed_role, 0) = 0 AND
                    HAS_PERMS_BY_NAME(QUOTENAME(candidate.name), 'SCHEMA', 'ALTER') = 1
            ) THEN 'schema-permission-alter'
            WHEN EXISTS (
                SELECT 1
                FROM sys.schemas AS candidate
                LEFT JOIN sys.database_principals AS candidate_owner
                    ON candidate_owner.principal_id = candidate.principal_id
                WHERE COALESCE(candidate_owner.is_fixed_role, 0) = 0 AND
                    HAS_PERMS_BY_NAME(QUOTENAME(candidate.name), 'SCHEMA', 'TAKE OWNERSHIP') = 1
            ) THEN 'schema-permission-take-ownership'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN (
                        'U', 'V', 'SO', 'SN', 'P', 'PC', 'X', 'RF', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF', 'SQ') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'ALTER') = 1
            ) THEN 'object-permission-alter'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                WHERE candidate.type IN (
                        'U', 'V', 'SO', 'SN', 'P', 'PC', 'X', 'RF', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF', 'SQ') AND
                    candidate.is_ms_shipped = 0 AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', 'TAKE OWNERSHIP') = 1
            ) THEN 'object-permission-take-ownership'
            WHEN EXISTS (
                SELECT 1
                FROM sys.database_permissions AS candidate
                INNER JOIN sys.columns AS candidate_column
                    ON candidate_column.object_id = candidate.major_id AND
                       candidate_column.column_id = candidate.minor_id
                WHERE candidate.class = 1 AND
                    candidate.minor_id > 0 AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name = 'UPDATE' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.user_token AS token) AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.major_id)) + '.' +
                            QUOTENAME(OBJECT_NAME(candidate.major_id)),
                        'OBJECT', 'UPDATE', candidate_column.name, 'COLUMN') = 1
            ) THEN 'column-update'
            WHEN EXISTS (
                SELECT 1
                FROM sys.fn_builtin_permissions('SERVER') AS candidate
                WHERE candidate.permission_name NOT IN ('CONNECT SQL', 'VIEW ANY DATABASE') AND
                    HAS_PERMS_BY_NAME(NULL, NULL, candidate.permission_name) = 1
            ) THEN 'server-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.fn_builtin_permissions('DATABASE') AS candidate
                WHERE candidate.permission_name NOT IN (
                        'CONNECT',
                        'SELECT',
                        'SHOWPLAN',
                        'VIEW DEFINITION',
                        'VIEW SECURITY DEFINITION',
                        'VIEW PERFORMANCE DEFINITION',
                        'VIEW ANY COLUMN ENCRYPTION KEY DEFINITION',
                        'VIEW ANY COLUMN MASTER KEY DEFINITION') AND
                    HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', candidate.permission_name) = 1
            ) THEN 'database-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.schemas AS candidate
                LEFT JOIN sys.database_principals AS candidate_owner
                    ON candidate_owner.principal_id = candidate.principal_id
                CROSS JOIN sys.fn_builtin_permissions('SCHEMA') AS candidate_permission
                WHERE COALESCE(candidate_owner.is_fixed_role, 0) = 0 AND
                    candidate_permission.permission_name NOT IN (
                        'SELECT', 'VIEW DEFINITION', 'VIEW CHANGE TRACKING') AND
                    HAS_PERMS_BY_NAME(QUOTENAME(candidate.name), 'SCHEMA', candidate_permission.permission_name) = 1
            ) THEN 'schema-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.objects AS candidate
                CROSS JOIN sys.fn_builtin_permissions('OBJECT') AS candidate_permission
                WHERE candidate.type IN (
                        'U', 'V', 'SO', 'SN', 'P', 'PC', 'X', 'RF', 'FN', 'IF', 'TF', 'FS', 'FT', 'AF', 'SQ') AND
                    candidate.is_ms_shipped = 0 AND
                    candidate_permission.permission_name NOT IN (
                        'SELECT', 'VIEW DEFINITION', 'VIEW CHANGE TRACKING') AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                        'OBJECT', candidate_permission.permission_name) = 1
            ) THEN 'object-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.database_permissions AS candidate
                INNER JOIN sys.columns AS candidate_column
                    ON candidate_column.object_id = candidate.major_id AND
                       candidate_column.column_id = candidate.minor_id
                WHERE candidate.class = 1 AND
                    candidate.minor_id > 0 AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name <> 'SELECT' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.user_token AS token) AND
                    HAS_PERMS_BY_NAME(
                        QUOTENAME(OBJECT_SCHEMA_NAME(candidate.major_id)) + '.' +
                            QUOTENAME(OBJECT_NAME(candidate.major_id)),
                        'OBJECT', candidate.permission_name, candidate_column.name, 'COLUMN') = 1
            ) THEN 'column-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.database_permissions AS candidate
                WHERE candidate.class = 4 AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name <> 'VIEW DEFINITION' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.user_token AS token)
                UNION ALL
                SELECT 1
                FROM sys.database_principals AS candidate
                WHERE candidate.type IN ('A', 'R') AND
                    candidate.owning_principal_id IN (SELECT token.principal_id FROM sys.user_token AS token)
            ) THEN 'database-principal-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.server_permissions AS candidate
                WHERE candidate.class = 101 AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name <> 'VIEW DEFINITION' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.login_token AS token)
                UNION ALL
                SELECT 1
                FROM sys.server_principals AS candidate
                WHERE candidate.type = 'R' AND
                    candidate.owning_principal_id IN (SELECT token.principal_id FROM sys.login_token AS token)
            ) THEN 'server-principal-permission-not-allowlisted'
            WHEN EXISTS (
                SELECT 1
                FROM sys.database_permissions AS candidate
                WHERE candidate.class NOT IN (0, 1, 3, 4) AND
                    candidate.state IN ('G', 'W') AND
                    candidate.permission_name <> 'VIEW DEFINITION' AND
                    candidate.grantee_principal_id IN (SELECT token.principal_id FROM sys.user_token AS token)
            ) THEN 'database-securable-permission-not-allowlisted'
            ELSE 'verified' END;
        """;

    private const string StrictVerificationSql =
        VerificationBeforeFunctionSelectSql + "\n" +
        StrictFunctionSelectSql + "\n" +
        VerificationAfterFunctionSelectSql;

    private const string InlineTableValuedSelectVerificationSql =
        VerificationBeforeFunctionSelectSql + "\n" +
        InlineTableValuedFunctionSelectSql + "\n" +
        VerificationAfterFunctionSelectSql;

    /// <summary>
    /// Under the <c>inline-table-valued-select</c> policy, after every other check passed: what the
    /// inline table-valued functions that the principal may select depend on. The principal reads
    /// a function's references through <c>sys.dm_sql_referenced_entities</c>, which reports them only
    /// with <c>VIEW DEFINITION</c> on the function, so a function the principal cannot see into
    /// rejects it. A function may depend only on tables and views of the same database, resolved
    /// and not caller-dependent, and the principal must be able to select each referenced column, or
    /// the whole object when the function names no column of it, such as for <c>COUNT(*)</c>; a
    /// referenced object that the principal cannot even see counts as one it may not select. That
    /// keeps an ownership chain through the function from showing anything the principal could not
    /// select itself. The functions are collected first, so the reference function only runs for
    /// functions the principal may select.
    /// </summary>
    private const string InlineFunctionDependencySql =
        """
        SET NOCOUNT ON;
        DECLARE @functions TABLE (
            object_id int NOT NULL PRIMARY KEY,
            qualified_name nvarchar(600) NOT NULL);
        DECLARE @dependencies TABLE (
            function_id int NOT NULL,
            referenced_class tinyint NULL,
            referenced_server_name sysname NULL,
            referenced_database_name sysname NULL,
            referenced_id int NULL,
            referenced_minor_id int NULL,
            referenced_minor_name sysname NULL,
            is_caller_dependent bit NULL,
            is_ambiguous bit NULL,
            is_all_columns_found bit NULL,
            is_incomplete bit NULL);

        INSERT INTO @functions (object_id, qualified_name)
        SELECT candidate.object_id,
               QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + N'.' + QUOTENAME(candidate.name)
        FROM sys.objects AS candidate
        WHERE candidate.type = 'IF' AND
            candidate.is_ms_shipped = 0 AND
            HAS_PERMS_BY_NAME(
                QUOTENAME(OBJECT_SCHEMA_NAME(candidate.object_id)) + '.' + QUOTENAME(candidate.name),
                'OBJECT', 'SELECT') = 1;

        INSERT INTO @dependencies (
            function_id,
            referenced_class,
            referenced_server_name,
            referenced_database_name,
            referenced_id,
            referenced_minor_id,
            referenced_minor_name,
            is_caller_dependent,
            is_ambiguous,
            is_all_columns_found,
            is_incomplete)
        SELECT candidate.object_id,
               reference.referenced_class,
               reference.referenced_server_name,
               reference.referenced_database_name,
               reference.referenced_id,
               reference.referenced_minor_id,
               reference.referenced_minor_name,
               reference.is_caller_dependent,
               reference.is_ambiguous,
               reference.is_all_columns_found,
               reference.is_incomplete
        FROM @functions AS candidate
        CROSS APPLY sys.dm_sql_referenced_entities(candidate.qualified_name, N'OBJECT') AS reference
        WHERE HAS_PERMS_BY_NAME(candidate.qualified_name, 'OBJECT', 'VIEW DEFINITION') = 1;

        SELECT CASE
            WHEN EXISTS (
                SELECT 1
                FROM @functions AS candidate
                WHERE COALESCE(HAS_PERMS_BY_NAME(candidate.qualified_name, 'OBJECT', 'VIEW DEFINITION'), 0) = 0
            ) THEN 'inline-function-definition-not-visible'
            WHEN EXISTS (
                SELECT 1
                FROM @dependencies AS dependency
                LEFT JOIN sys.objects AS referenced
                    ON referenced.object_id = dependency.referenced_id
                WHERE COALESCE(dependency.referenced_class, 0) <> 1 OR
                    dependency.referenced_server_name IS NOT NULL OR
                    dependency.referenced_database_name IS NOT NULL OR
                    COALESCE(dependency.is_caller_dependent, 1) = 1 OR
                    COALESCE(dependency.is_ambiguous, 1) = 1 OR
                    COALESCE(dependency.is_all_columns_found, 0) = 0 OR
                    COALESCE(dependency.is_incomplete, 0) = 1 OR
                    dependency.referenced_id IS NULL OR
                    referenced.type NOT IN ('U', 'V') OR
                    referenced.is_ms_shipped = 1
            ) THEN 'inline-function-dependency-not-allowed'
            WHEN EXISTS (
                SELECT 1
                FROM @dependencies AS dependency
                LEFT JOIN sys.objects AS referenced
                    ON referenced.object_id = dependency.referenced_id
                WHERE referenced.object_id IS NULL
                UNION ALL
                SELECT 1
                FROM @dependencies AS dependency
                INNER JOIN sys.objects AS referenced
                    ON referenced.object_id = dependency.referenced_id
                WHERE (
                        COALESCE(dependency.referenced_minor_id, 0) = 0 AND
                        NOT EXISTS (
                            SELECT 1
                            FROM @dependencies AS column_dependency
                            WHERE column_dependency.function_id = dependency.function_id AND
                                column_dependency.referenced_id = dependency.referenced_id AND
                                column_dependency.referenced_minor_id > 0
                        ) AND
                        COALESCE(HAS_PERMS_BY_NAME(
                            QUOTENAME(OBJECT_SCHEMA_NAME(referenced.object_id)) + '.' + QUOTENAME(referenced.name),
                            'OBJECT', 'SELECT'), 0) = 0
                    ) OR (
                        dependency.referenced_minor_id > 0 AND
                        COALESCE(HAS_PERMS_BY_NAME(
                            QUOTENAME(OBJECT_SCHEMA_NAME(referenced.object_id)) + '.' + QUOTENAME(referenced.name),
                            'OBJECT', 'SELECT', dependency.referenced_minor_name, 'COLUMN'), 0) = 0
                    )
            ) THEN 'inline-function-dependency-not-selectable'
            ELSE 'verified' END;
        SET NOCOUNT OFF;
        """;

    private readonly SqlServerFunctionPolicy _functionPolicy;

    public SqlServerDatabaseReadProvider()
        : this(SqlServerFunctionPolicy.Strict)
    {
    }

    public SqlServerDatabaseReadProvider(SqlServerFunctionPolicy functionPolicy)
    {
        _functionPolicy = functionPolicy;
    }

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

    /// <summary>
    /// SqlClient resets a pooled connection with <c>sp_reset_connection</c> on its next use. Its
    /// connection resiliency would replace a broken connection during the next command, after the
    /// tool looked at the connection; without it the request fails and the next one verifies its
    /// new connection.
    /// </summary>
    public string CreateReusableConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            ConnectRetryCount = 0
        };

        return builder.ConnectionString;
    }

    public string GetPhysicalConnectionId(DbConnection connection)
    {
        return ((SqlConnection)connection).ClientConnectionId.ToString("N");
    }

    public Task ClearConnectionPoolAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        SqlConnection.ClearPool((SqlConnection)connection);

        return Task.CompletedTask;
    }

    /// <summary>
    /// The SQL Server verification has no minimum server version of its own.
    /// </summary>
    public void EnsureSupportedServer(DbConnection connection)
    {
    }

    /// <summary>
    /// Verifies the principal. The catalog reads take shared locks on catalog rows and metadata
    /// locks on the securables they report, so a concurrent change of logins, users or grants can
    /// block them or choose them as deadlock victim. The session therefore runs with
    /// <c>DEADLOCK_PRIORITY LOW</c>, so the diagnostic request and never the other session's change
    /// is the victim, and with the profile's lock timeout, so a blocked read fails instead of using
    /// up the statement timeout. A verification that was the deadlock victim or timed out on a lock
    /// runs again while the request's timeout leaves time for it, at most
    /// <see cref="MaximumVerificationAttempts"/> times; after that its error propagates and the
    /// request fails with that classification. A failed attempt never counts as verified.
    /// </summary>
    public async Task<DatabaseReadPrincipalVerification> VerifyReadOnlyPrincipalAsync(
        DbConnection connection,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using (var session = connection.CreateCommand())
        {
            session.CommandText = FormattableString.Invariant(
                $"SET DEADLOCK_PRIORITY LOW; SET LOCK_TIMEOUT {limits.LockTimeoutMilliseconds};");
            session.CommandTimeout = limits.TimeoutSeconds;
            await session.ExecuteNonQueryAsync(cancellationToken);
        }

        var timeout = TimeSpan.FromSeconds(limits.TimeoutSeconds);
        var elapsed = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadVerificationAsync(connection, timeout - elapsed.Elapsed, cancellationToken);
            }
            catch (SqlException exception) when (
                IsLockConflict(exception) && attempt < MaximumVerificationAttempts)
            {
                var backoff = VerificationRetryBackoff(attempt);
                if (elapsed.Elapsed + backoff + MinimumVerificationAttemptTime >= timeout)
                {
                    throw;
                }

                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    internal static bool IsLockConflict(SqlException exception)
    {
        return exception.Errors
            .Cast<SqlError>()
            .Any(error => error.Number is DeadlockVictim or LockRequestTimeout);
    }

    /// <summary>
    /// A short, growing and randomized pause, so verifications that were victims of the same
    /// deadlock do not meet again at once.
    /// </summary>
    internal static TimeSpan VerificationRetryBackoff(int attempt)
    {
        var ceiling = Math.Min(50 * (1 << Math.Min(attempt, 4)), 500);

        return TimeSpan.FromMilliseconds(Random.Shared.Next(ceiling / 2, ceiling + 1));
    }

    /// <summary>
    /// Runs the checks of the profile's function policy. Under <c>inline-table-valued-select</c>
    /// the dependencies of the inline functions are read only when every other check passed, in a
    /// second statement within the same attempt and timeout.
    /// </summary>
    private async Task<DatabaseReadPrincipalVerification> ReadVerificationAsync(
        DbConnection connection,
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var verification = await ReadCheckAsync(
            connection,
            _functionPolicy == SqlServerFunctionPolicy.InlineTableValuedSelect
                ? InlineTableValuedSelectVerificationSql
                : StrictVerificationSql,
            remaining,
            cancellationToken);
        if (!verification.IsReadOnly || _functionPolicy != SqlServerFunctionPolicy.InlineTableValuedSelect)
        {
            return verification;
        }

        return await ReadCheckAsync(
            connection,
            InlineFunctionDependencySql,
            remaining - elapsed.Elapsed,
            cancellationToken);
    }

    private static async Task<DatabaseReadPrincipalVerification> ReadCheckAsync(
        DbConnection connection,
        string sql,
        TimeSpan remaining,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
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

    /// <summary>
    /// Sets the session options every diagnostic runs with. The verification sets them as well, but
    /// a request on a reused connection skips it, and the connection's reset took them away.
    /// </summary>
    public async Task ConfigureReadOnlyTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = FormattableString.Invariant(
            $"SET DEADLOCK_PRIORITY LOW; SET LOCK_TIMEOUT {limits.LockTimeoutMilliseconds};");
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

    /// <summary>
    /// <c>datetimeoffset</c> keeps the offset, and <c>CAST(@value AS datetime2)</c>, <c>DATEPART</c>
    /// and <c>SWITCHOFFSET</c> read the local part of it, so the value stays as the caller wrote it.
    /// </summary>
    public DateTimeOffset NormalizeInstant(DateTimeOffset instant)
    {
        return instant;
    }

    public DatabaseReadProviderFailure ClassifyException(
        DbException exception,
        DatabaseReadExecutionStage stage)
    {
        var sqlException = DatabaseReadExceptionInspector.Find<SqlException>(exception);
        if (sqlException is not null)
        {
            var errorNumbers = sqlException.Errors
                .Cast<SqlError>()
                .Select(error => error.Number)
                .ToArray();

            return ClassifySqlErrorNumbers(errorNumbers, exception, stage);
        }

        if (DatabaseReadExceptionInspector.IsCertificateValidationFailure(exception))
        {
            return UntrustedCertificateFailure(null);
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

    internal static DatabaseReadProviderFailure ClassifySqlErrorNumbers(
        IReadOnlyList<int> errorNumbers,
        Exception exception,
        DatabaseReadExecutionStage stage)
    {
        ArgumentNullException.ThrowIfNull(errorNumbers);
        ArgumentNullException.ThrowIfNull(exception);

        var providerCode = errorNumbers.Count > 0
            ? errorNumbers[0].ToString(CultureInfo.InvariantCulture)
            : null;

        if (DatabaseReadExceptionInspector.IsCertificateValidationFailure(exception))
        {
            return UntrustedCertificateFailure(providerCode);
        }

        foreach (var errorNumber in errorNumbers)
        {
            var failure = ClassifySqlError(errorNumber, providerCode, stage);
            if (failure is not null)
            {
                return failure;
            }
        }

        if (stage == DatabaseReadExecutionStage.ConnectionOpen
            || DatabaseReadExceptionInspector.IsConnectivityFailure(exception))
        {
            return ConnectionFailure(providerCode);
        }

        return Failure(
            "database-failure",
            providerCode,
            false,
            "The database rejected or could not complete the diagnostic operation.");
    }

    private static DatabaseReadProviderFailure? ClassifySqlError(
        int number,
        string? providerCode,
        DatabaseReadExecutionStage stage)
    {
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
            -2 when stage == DatabaseReadExecutionStage.ConnectionOpen =>
                ConnectionFailure(providerCode),
            -2 => Failure("statement-timeout", providerCode, false,
                "The database cancelled the statement after its configured execution boundary."),
            1222 => Failure("lock-timeout", providerCode, false,
                "The database could not acquire a required lock within the configured boundary."),
            1205 => Failure("deadlock", providerCode, true, "retry-operation",
                "The database selected the diagnostic statement as a deadlock victim."),
            4060 => Failure("database-not-found", providerCode, false,
                "The configured database does not exist or is unavailable to the principal."),
            18452 or 18456 => Failure("authentication-failed", providerCode, false,
                "The database rejected the configured principal."),
            SecurityUntrustedRoot or CertificateUntrustedRoot or CertificateChaining =>
                UntrustedCertificateFailure(providerCode),
            0 or 2 or 20 or 40 or 53 or 64 or 233 or 10053 or 10054 or 10060 or 11001 => Failure(
                "connection-failed",
                providerCode,
                true,
                "network-access-required",
                "The database connection could not be established or was interrupted."),
            _ => null
        };
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

    private static DatabaseReadProviderFailure UntrustedCertificateFailure(string? providerCode)
    {
        return Failure(
            "tls-certificate-untrusted",
            providerCode,
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
                            QUOTENAME(candidate_schema.name) + '.' + QUOTENAME(candidate.name),
                            'OBJECT',
                            'SELECT',
                            selectable_column.name,
                            'COLUMN') = 1
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
                            QUOTENAME(candidate_schema.name) + '.' + QUOTENAME(candidate.name),
                            'OBJECT',
                            'SELECT',
                            selectable_column.name,
                            'COLUMN') = 1
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
                      @qualifiedObjectName,
                      'OBJECT',
                      'SELECT',
                      candidate.name,
                      'COLUMN') = 1
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
                   LOWER(REPLACE(candidate.type_desc, '_', '-')),
                   candidate.is_unique,
                   candidate.is_primary_key,
                   candidate.has_filter,
                   CASE
                       WHEN HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') = 1
                       THEN candidate.filter_definition
                       ELSE NULL
                   END,
                   indexed_column.key_ordinal,
                   indexed_column.index_column_id,
                   indexed_column.is_included_column,
                   indexed_column.is_descending_key,
                   schema_column.name,
                   CASE
                       WHEN HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') = 1
                       THEN computed_column.definition
                       ELSE NULL
                   END
            FROM sys.indexes AS candidate
            INNER JOIN sys.index_columns AS indexed_column
                ON indexed_column.object_id = candidate.object_id
               AND indexed_column.index_id = candidate.index_id
            INNER JOIN sys.columns AS schema_column
                ON schema_column.object_id = indexed_column.object_id
               AND schema_column.column_id = indexed_column.column_id
            LEFT JOIN sys.computed_columns AS computed_column
                ON computed_column.object_id = schema_column.object_id
               AND computed_column.column_id = schema_column.column_id
            WHERE candidate.object_id = @objectId
              AND candidate.name IS NOT NULL
              AND candidate.type IN (1, 2)
              AND candidate.is_hypothetical = 0
              AND candidate.is_disabled = 0
              AND indexed_column.column_id > 0
              AND (
                  HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') = 1 OR
                  NOT EXISTS (
                      SELECT 1
                      FROM sys.index_columns AS computed_indexed_column
                      INNER JOIN sys.computed_columns AS indexed_computed_column
                          ON indexed_computed_column.object_id = computed_indexed_column.object_id
                         AND indexed_computed_column.column_id = computed_indexed_column.column_id
                      WHERE computed_indexed_column.object_id = candidate.object_id
                        AND computed_indexed_column.index_id = candidate.index_id
                        AND computed_indexed_column.is_included_column = 0
                  )
              )
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
                        @qualifiedObjectName,
                        'OBJECT',
                        'SELECT',
                        denied_schema_column.name,
                        'COLUMN') <> 1
              )
              AND (
                  HAS_PERMS_BY_NAME(@qualifiedObjectName, 'OBJECT', 'SELECT') = 1 OR
                  HAS_PERMS_BY_NAME(
                      @qualifiedObjectName,
                      'OBJECT',
                      'SELECT',
                      schema_column.name,
                      'COLUMN') = 1
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
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
                byName.Add(name, builder);
                builders.Add(builder);
            }

            var columnName = reader.GetString(10);
            if (reader.GetBoolean(8))
            {
                builder.IncludedColumns.Add(columnName);
            }
            else
            {
                var expression = reader.IsDBNull(11) ? null : reader.GetString(11);
                builder.KeyColumns.Add(new DatabaseSchemaIndexColumnResponse
                {
                    Position = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Kind = expression is null ? "column" : "expression",
                    Name = columnName,
                    Expression = expression,
                    Direction = reader.GetBoolean(9) ? "descending" : "ascending"
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
                    .Where(column => column.Kind == "column" && column.Name is not null)
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
        int objectId,
        DatabaseReadLimits limits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = limits.TimeoutSeconds;
        command.CommandText =
            """
            SELECT candidate.object_id,
                   candidate.name,
                   CONVERT(bit, CASE
                       WHEN candidate.is_not_trusted = 0 AND candidate.is_disabled = 0 THEN 1
                       ELSE 0
                   END),
                   source_object.object_id,
                   source_schema.name,
                   source_object.name,
                   target_object.object_id,
                   target_schema.name,
                   target_object.name,
                   column_pair.constraint_column_id,
                   source_column.name,
                   target_column.name
            FROM sys.foreign_keys AS candidate
            INNER JOIN sys.foreign_key_columns AS column_pair
                ON column_pair.constraint_object_id = candidate.object_id
            INNER JOIN sys.tables AS source_object
                ON source_object.object_id = candidate.parent_object_id
            INNER JOIN sys.schemas AS source_schema
                ON source_schema.schema_id = source_object.schema_id
            INNER JOIN sys.columns AS source_column
                ON source_column.object_id = source_object.object_id
               AND source_column.column_id = column_pair.parent_column_id
            INNER JOIN sys.tables AS target_object
                ON target_object.object_id = candidate.referenced_object_id
            INNER JOIN sys.schemas AS target_schema
                ON target_schema.schema_id = target_object.schema_id
            INNER JOIN sys.columns AS target_column
                ON target_column.object_id = target_object.object_id
               AND target_column.column_id = column_pair.referenced_column_id
            WHERE (candidate.parent_object_id = @objectId OR
                   candidate.referenced_object_id = @objectId)
              AND NOT EXISTS (
                  SELECT 1
                  FROM sys.foreign_key_columns AS denied_pair
                  INNER JOIN sys.tables AS denied_source_object
                      ON denied_source_object.object_id = candidate.parent_object_id
                  INNER JOIN sys.schemas AS denied_source_schema
                      ON denied_source_schema.schema_id = denied_source_object.schema_id
                  INNER JOIN sys.columns AS denied_source_column
                      ON denied_source_column.object_id = denied_source_object.object_id
                     AND denied_source_column.column_id = denied_pair.parent_column_id
                  INNER JOIN sys.tables AS denied_target_object
                      ON denied_target_object.object_id = candidate.referenced_object_id
                  INNER JOIN sys.schemas AS denied_target_schema
                      ON denied_target_schema.schema_id = denied_target_object.schema_id
                  INNER JOIN sys.columns AS denied_target_column
                      ON denied_target_column.object_id = denied_target_object.object_id
                     AND denied_target_column.column_id = denied_pair.referenced_column_id
                  WHERE denied_pair.constraint_object_id = candidate.object_id
                    AND (
                        (
                            HAS_PERMS_BY_NAME(
                                QUOTENAME(denied_source_schema.name) + '.' +
                                QUOTENAME(denied_source_object.name),
                                'OBJECT',
                                'SELECT') <> 1 AND
                            HAS_PERMS_BY_NAME(
                                QUOTENAME(denied_source_schema.name) + '.' +
                                QUOTENAME(denied_source_object.name),
                                'OBJECT',
                                'SELECT',
                                denied_source_column.name,
                                'COLUMN') <> 1
                        ) OR
                        (
                            HAS_PERMS_BY_NAME(
                                QUOTENAME(denied_target_schema.name) + '.' +
                                QUOTENAME(denied_target_object.name),
                                'OBJECT',
                                'SELECT') <> 1 AND
                            HAS_PERMS_BY_NAME(
                                QUOTENAME(denied_target_schema.name) + '.' +
                                QUOTENAME(denied_target_object.name),
                                'OBJECT',
                                'SELECT',
                                denied_target_column.name,
                                'COLUMN') <> 1
                        )
                    )
              )
            ORDER BY candidate.object_id, column_pair.constraint_column_id;
            """;
        AddParameter(command, "@objectId", objectId);

        var builders = new List<SqlServerRelationshipBuilder>();
        var byId = new Dictionary<int, SqlServerRelationshipBuilder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var relationshipId = reader.GetInt32(0);
            if (!byId.TryGetValue(relationshipId, out var builder))
            {
                builder = new SqlServerRelationshipBuilder(
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetInt32(3),
                    new DatabaseSchemaRelationshipObjectResponse
                    {
                        Schema = reader.GetString(4),
                        Name = reader.GetString(5),
                        SqlIdentifier = Quote(reader.GetString(4), reader.GetString(5))
                    },
                    reader.GetInt32(6),
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
                Position = reader.GetInt32(9),
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
            "sql-server",
            classification,
            providerCode,
            transient,
            retryHint,
            message);
    }

    private sealed record SqlServerObjectDescriptor(
        int ObjectId,
        string Schema,
        string Name,
        string Kind);

    private sealed class SqlServerIndexBuilder(
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

    private sealed class SqlServerRelationshipBuilder(
        string name,
        bool isValidated,
        int sourceObjectId,
        DatabaseSchemaRelationshipObjectResponse source,
        int targetObjectId,
        DatabaseSchemaRelationshipObjectResponse target)
    {
        public int SourceObjectId { get; } = sourceObjectId;

        public int TargetObjectId { get; } = targetObjectId;

        public List<DatabaseSchemaRelationshipColumnPairResponse> ColumnPairs { get; } = [];

        public DatabaseSchemaRelationshipResponse Build() => new()
        {
            Name = name,
            IsValidated = isValidated,
            Source = source,
            Target = target,
            ColumnPairs = ColumnPairs
        };
    }
}