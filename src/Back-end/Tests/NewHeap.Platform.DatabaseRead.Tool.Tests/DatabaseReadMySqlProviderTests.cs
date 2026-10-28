using AwesomeAssertions;
using MySqlConnector;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The MySQL and MariaDB provider on real servers, through the tool's public entry point: a
/// SELECT-only principal reads, schema evidence comes from <c>information_schema</c> filtered to
/// what the principal may select, failures keep their safe classifications, the transaction is
/// read only with the flavor's statement and lock limits, and a profile never runs against the
/// other flavor. Every test gets a database of its own on the flavor's server that the test
/// process shares.
/// </summary>
public sealed class DatabaseReadMySqlProviderTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

    private static readonly Guid ProjectId = Guid.Parse("9f6e1ca4-96e4-4f6a-8820-738693649dc3");

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task ProfileReadsWithASelectOnlyPrincipal(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var admin = new MySqlConnectionStringBuilder(await server.CreateDatabaseAsync("diag"));
        var database = admin.Database;
        var reader = database + "_reader";
        var mySql = flavor == DatabaseReadMySqlFlavor.MySql;
        var hiddenIndex = mySql ? "INVISIBLE" : "IGNORED";
        await DatabaseReadMySqlTestServer.ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE diagnostic_owners (
                tenant_id char(36) NOT NULL,
                id char(36) NOT NULL,
                name varchar(100) NOT NULL,
                PRIMARY KEY (tenant_id, id)
            );
            CREATE TABLE restricted_projects (
                id char(36) NOT NULL PRIMARY KEY,
                secret varchar(100) NOT NULL
            );
            CREATE TABLE diagnostic_projects (
                id char(36) NOT NULL PRIMARY KEY,
                tenant_id char(36) NOT NULL,
                owner_id char(36) NOT NULL,
                name varchar(100) NOT NULL,
                status varchar(32) NOT NULL,
                restricted_project_id char(36) NULL,
                CONSTRAINT fk_diagnostic_projects_diagnostic_owners
                    FOREIGN KEY (tenant_id, owner_id) REFERENCES diagnostic_owners (tenant_id, id),
                CONSTRAINT fk_diagnostic_projects_restricted_projects
                    FOREIGN KEY (restricted_project_id) REFERENCES restricted_projects (id),
                KEY ix_diagnostic_projects_name_id (name ASC, id DESC),
                KEY ix_diagnostic_projects_hidden (status) {hiddenIndex}
                {(mySql ? ", KEY ix_diagnostic_projects_lower_name ((lower(name)))" : string.Empty)}
            );
            CREATE TABLE diagnostic_tasks (
                id char(36) NOT NULL PRIMARY KEY,
                project_id char(36) NOT NULL,
                CONSTRAINT fk_diagnostic_tasks_diagnostic_projects
                    FOREIGN KEY (project_id) REFERENCES diagnostic_projects (id)
            );
            CREATE TABLE column_limited_projects (
                id char(36) NOT NULL PRIMARY KEY,
                name varchar(100) NOT NULL,
                status varchar(32) NOT NULL,
                KEY ix_column_limited_projects_name (name),
                KEY ix_column_limited_projects_status (status)
                {(mySql
                    ? ", KEY ix_column_limited_projects_lower_name ((lower(name))), KEY ix_column_limited_projects_lower_status ((lower(status)))"
                    : string.Empty)}
            );
            INSERT INTO restricted_projects (id, secret)
            VALUES ('1cb9703f-d328-4e35-9305-0f33c3f71374', 'Secret');
            INSERT INTO diagnostic_owners (tenant_id, id, name)
            VALUES ('1c80def1-f300-4bd2-920d-a3c89578d357', '82783a25-ddf3-4a28-9d26-e1305cc93b4f', 'Diagnostic owner');
            INSERT INTO diagnostic_projects (id, tenant_id, owner_id, name, status, restricted_project_id)
            VALUES ('{ProjectId}', '1c80def1-f300-4bd2-920d-a3c89578d357', '82783a25-ddf3-4a28-9d26-e1305cc93b4f',
                    'Diagnostic project', 'active', '1cb9703f-d328-4e35-9305-0f33c3f71374');
            INSERT INTO diagnostic_tasks (id, project_id)
            VALUES ('85b3eb6a-5834-4c8f-a192-8901744e50f9', '{ProjectId}');
            INSERT INTO column_limited_projects (id, name, status)
            VALUES ('ea6e418b-dd0d-45b3-8853-57cc5e843098', 'Column limited', 'active');
            CREATE USER `{reader}`@`%` IDENTIFIED BY '{ReaderPassword}';
            GRANT SELECT ON `{database}`.diagnostic_projects TO `{reader}`@`%`;
            GRANT SELECT ON `{database}`.diagnostic_owners TO `{reader}`@`%`;
            GRANT SELECT ON `{database}`.diagnostic_tasks TO `{reader}`@`%`;
            GRANT SELECT (id, name) ON `{database}`.column_limited_projects TO `{reader}`@`%`;
            """);
        var readerConnectionString = DatabaseReadMySqlTestServer.AccountConnectionString(
            admin.ConnectionString,
            reader,
            ReaderPassword);

        try
        {
            using (var workspace = new DatabaseReadTestWorkspace(server.ProviderName, readerConnectionString))
            {
                using var input = DatabaseReadTestWorkspace.Request(
                    "SELECT id, name FROM diagnostic_projects WHERE id = @id LIMIT 10",
                    [new { name = "id", type = "uuid", value = ProjectId.ToString() }]);
                using var output = new MemoryStream();

                var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                    ["query", "--profiles", workspace.ProfileCatalogPath, "--environment", "Production"],
                    input,
                    output);

                exitCode.Should().Be(0, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
                using var response = DatabaseReadTestWorkspace.ParseOutput(output);
                var root = response.RootElement;
                root.GetProperty("target").GetProperty("provider").GetString().Should().Be(server.ProviderName);
                root.GetProperty("target").GetProperty("environment").GetString().Should().Be("Production");
                root.GetProperty("target").GetProperty("readOnlyVerified").GetBoolean().Should().BeTrue();
                var row = root.GetProperty("result").GetProperty("rows")[0];
                row[0].GetString().Should().Be(ProjectId.ToString());
                row[1].GetString().Should().Be("Diagnostic project");
            }

            await AssertSchemaEvidenceAsync(server.ProviderName, readerConnectionString, database, mySql);
            await AssertColumnLimitedEvidenceAsync(server.ProviderName, readerConnectionString, database, mySql);
            await AssertSchemaFailureAsync(
                server.ProviderName,
                readerConnectionString,
                DatabaseReadTestWorkspace.SchemaRequest("indexes", database, "restricted_projects"),
                "schema-object-not-found");
            await AssertClassifiedFailureAsync(
                server.ProviderName,
                readerConnectionString,
                "SELECT missing_column FROM diagnostic_projects LIMIT 1",
                "column-not-found",
                "1054");
            await AssertClassifiedFailureAsync(
                server.ProviderName,
                readerConnectionString,
                "SELECT id FROM missing_projects LIMIT 1",
                "object-not-found",
                "1146");
            await AssertClassifiedFailureAsync(
                server.ProviderName,
                readerConnectionString,
                "SELECT id FROM restricted_projects LIMIT 1",
                "permission-denied",
                "1142");

            using (var rows = await RunQueryAsync(
                       server.ProviderName,
                       readerConnectionString,
                       "SELECT 1 AS value_number UNION ALL SELECT 2 AS value_number",
                       4,
                       maximumRows: 1))
            {
                rows.RootElement.TryGetProperty("result", out _).Should().BeFalse();
                rows.RootElement.GetProperty("error").GetProperty("code").GetString()
                    .Should().Be("result-row-limit-exceeded");
            }

            using (var quoted = await RunQueryAsync(
                       server.ProviderName,
                       readerConnectionString,
                       $"SELECT `id` AS `Update`, `name` AS `Share` FROM `{database}`.`diagnostic_projects` LIMIT 1",
                       0))
            {
                quoted.RootElement.GetProperty("result").GetProperty("columns").EnumerateArray()
                    .Select(column => column.GetProperty("name").GetString())
                    .Should().Equal("Update", "Share");
            }

            foreach (var sql in new[] { "SELECT SLEEP(0) AS slept", "SELECT `sleep`(0) AS slept", "SELECT GET_LOCK('probe', 0)" })
            {
                using var refused = await RunQueryAsync(server.ProviderName, readerConnectionString, sql, 4);
                refused.RootElement.TryGetProperty("result", out _).Should().BeFalse();
                refused.RootElement.GetProperty("error").GetProperty("code").GetString()
                    .Should().Be("function-not-allowed");
            }

            using (var elevated = await RunQueryAsync(server.ProviderName, admin.ConnectionString, "SELECT 1", 4))
            {
                var error = elevated.RootElement.GetProperty("error");
                error.GetProperty("code").GetString().Should().Be("read-only-principal-not-verified");
                error.GetProperty("verificationCheck").GetString().Should().Be("global-privilege");
            }

            await using var readerConnection = new MySqlConnection(readerConnectionString);
            await readerConnection.OpenAsync();
            await using var update = readerConnection.CreateCommand();
            update.CommandText = "UPDATE diagnostic_projects SET name = 'Changed' WHERE id = @id";
            update.Parameters.AddWithValue("@id", ProjectId.ToString());
            var writeAction = () => update.ExecuteNonQueryAsync();

            await writeAction.Should().ThrowAsync<MySqlException>();
        }
        finally
        {
            await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, $"DROP USER `{reader}`@`%`;");
        }
    }

    /// <summary>
    /// The flavor follows from the server's version, so a <c>mysql</c> profile against MariaDB and
    /// a <c>mariadb</c> profile against MySQL stop before the principal is verified.
    /// </summary>
    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task ProfileOfTheOtherFlavorIsRefused(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var otherProvider = flavor == DatabaseReadMySqlFlavor.MySql ? "mariadb" : "mysql";
        var connectionString = await server.GetConnectionStringAsync();

        using var response = await RunQueryAsync(otherProvider, connectionString, "SELECT 1", 3);

        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("provider-flavor-mismatch");
        response.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    /// <summary>
    /// The diagnostic transaction starts read only, so not even an administrator writes in it, and
    /// carries the flavor's statement limit, <c>max_execution_time</c> in milliseconds on MySQL and
    /// <c>max_statement_time</c> in seconds on MariaDB, and the lock limits in whole seconds, the
    /// profile's 2.5 seconds rounded up. The server then ends a statement that runs too long with
    /// its own error, which the provider classifies as a statement timeout.
    /// </summary>
    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task TransactionIsReadOnlyWithTheFlavorsTimeLimits(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var admin = await server.CreateDatabaseAsync("limits");
        await DatabaseReadMySqlTestServer.ExecuteAsync(
            admin,
            """
            CREATE TABLE digits (d int NOT NULL PRIMARY KEY);
            INSERT INTO digits (d) VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9);
            CREATE TABLE numbers (n int NOT NULL PRIMARY KEY);
            INSERT INTO numbers (n)
            SELECT hundreds.d * 100 + tens.d * 10 + ones.d
            FROM digits AS hundreds CROSS JOIN digits AS tens CROSS JOIN digits AS ones;
            """);
        var provider = new MySqlDatabaseReadProvider(server.Kind);
        var limits = new DatabaseReadLimits(10, 7, 2_500, 65_536, 4_096, 8_192);

        await using (var connection = provider.CreateConnection(admin, Guid.NewGuid().ToString("N"), limits))
        {
            await connection.OpenAsync();
            await using var transaction = await provider.BeginReadOnlyTransactionAsync(connection, CancellationToken.None);
            await provider.ConfigureReadOnlyTransactionAsync(connection, transaction, limits, CancellationToken.None);

            await using (var settings = connection.CreateCommand())
            {
                settings.Transaction = transaction;
                settings.CommandText = flavor == DatabaseReadMySqlFlavor.MySql
                    ? "SELECT @@SESSION.max_execution_time, @@SESSION.innodb_lock_wait_timeout, @@SESSION.lock_wait_timeout"
                    : "SELECT @@SESSION.max_statement_time, @@SESSION.innodb_lock_wait_timeout, @@SESSION.lock_wait_timeout";
                await using var values = await settings.ExecuteReaderAsync();
                (await values.ReadAsync()).Should().BeTrue();
                Convert.ToDecimal(values.GetValue(0)).Should().Be(flavor == DatabaseReadMySqlFlavor.MySql ? 7_000 : 7);
                Convert.ToInt64(values.GetValue(1)).Should().Be(3);
                Convert.ToInt64(values.GetValue(2)).Should().Be(3);
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO digits (d) VALUES (10)";
            var write = () => insert.ExecuteNonQueryAsync();

            (await write.Should().ThrowAsync<MySqlException>()).Which.Number.Should().Be(1792);
        }

        var shortLimits = limits with { TimeoutSeconds = 1 };
        await using (var connection = provider.CreateConnection(admin, Guid.NewGuid().ToString("N"), shortLimits))
        {
            await connection.OpenAsync();
            await using var transaction = await provider.BeginReadOnlyTransactionAsync(connection, CancellationToken.None);
            await provider.ConfigureReadOnlyTransactionAsync(connection, transaction, shortLimits, CancellationToken.None);
            await using var slow = connection.CreateCommand();
            slow.Transaction = transaction;
            slow.CommandTimeout = 0;
            slow.CommandText =
                "SELECT SUM(a.n + b.n + c.n) FROM numbers AS a CROSS JOIN numbers AS b CROSS JOIN numbers AS c";
            var read = () => slow.ExecuteScalarAsync();

            var exception = (await read.Should().ThrowAsync<MySqlException>()).Which;
            exception.Number.Should().Be(flavor == DatabaseReadMySqlFlavor.MySql ? 3024 : 1969);
            provider.ClassifyException(exception, DatabaseReadExecutionStage.QueryExecution)
                .Classification.Should().Be("statement-timeout");
        }
    }

    /// <summary>
    /// A table that another session holds a write lock on makes the diagnostic wait for its
    /// metadata lock only for the profile's lock timeout, and the tool reports the lock timeout.
    /// </summary>
    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task LockedTableEndsTheQueryAfterTheLockTimeout(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var admin = new MySqlConnectionStringBuilder(await server.CreateDatabaseAsync("locks"));
        var reader = admin.Database + "_reader";
        await DatabaseReadMySqlTestServer.ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE items (id int NOT NULL PRIMARY KEY);
            INSERT INTO items (id) VALUES (1);
            CREATE USER `{reader}`@`%` IDENTIFIED BY '{ReaderPassword}';
            GRANT SELECT ON `{admin.Database}`.items TO `{reader}`@`%`;
            """);
        var readerConnectionString = DatabaseReadMySqlTestServer.AccountConnectionString(
            admin.ConnectionString,
            reader,
            ReaderPassword);

        try
        {
            await using var holder = new MySqlConnection(
                new MySqlConnectionStringBuilder(admin.ConnectionString) { Pooling = false }.ConnectionString);
            await holder.OpenAsync();
            await using (var lockTable = holder.CreateCommand())
            {
                lockTable.CommandText = "LOCK TABLES items WRITE";
                await lockTable.ExecuteNonQueryAsync();
            }

            using var response = await RunQueryAsync(
                server.ProviderName,
                readerConnectionString,
                "SELECT id FROM items LIMIT 1",
                5);

            var error = response.RootElement.GetProperty("error");
            error.GetProperty("classification").GetString().Should().Be("lock-timeout");
            error.GetProperty("providerCode").GetString().Should().Be("1205");
            error.GetProperty("stage").GetString().Should().Be("query-execution");

            await using var unlock = holder.CreateCommand();
            unlock.CommandText = "UNLOCK TABLES";
            await unlock.ExecuteNonQueryAsync();
        }
        finally
        {
            await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, $"DROP USER `{reader}`@`%`;");
        }
    }

    [Theory]
    [InlineData(1146, "object-not-found", false)]
    [InlineData(1054, "column-not-found", false)]
    [InlineData(1142, "permission-denied", false)]
    [InlineData(1143, "permission-denied", false)]
    [InlineData(1064, "syntax-error", false)]
    [InlineData(3024, "statement-timeout", false)]
    [InlineData(1969, "statement-timeout", false)]
    [InlineData(1205, "lock-timeout", false)]
    [InlineData(1213, "deadlock", true)]
    [InlineData(1049, "database-not-found", false)]
    [InlineData(1045, "authentication-failed", false)]
    [InlineData(2013, "connection-failed", true)]
    [InlineData(1792, "database-failure", false)]
    public void ErrorNumbersKeepTheirSafeClassification(int number, string classification, bool transient)
    {
        foreach (var flavor in new[] { DatabaseProviderKind.MySql, DatabaseProviderKind.MariaDb })
        {
            var provider = new MySqlDatabaseReadProvider(flavor);

            var failure = provider.ClassifyErrorNumber(
                number,
                new InvalidOperationException("Password=private-canary"),
                DatabaseReadExecutionStage.QueryExecution);

            failure.Classification.Should().Be(classification);
            failure.Transient.Should().Be(transient);
            failure.Provider.Should().Be(provider.Name);
            failure.ProviderCode.Should().Be(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            failure.Message.Should().NotContain("private-canary");
        }
    }

    [Fact]
    public void AccessToTheDatabaseAtConnectionOpenIsADatabaseNotFound()
    {
        var provider = new MySqlDatabaseReadProvider(DatabaseProviderKind.MySql);

        provider.ClassifyErrorNumber(1044, new InvalidOperationException(), DatabaseReadExecutionStage.ConnectionOpen)
            .Classification.Should().Be("database-not-found");
        provider.ClassifyErrorNumber(1044, new InvalidOperationException(), DatabaseReadExecutionStage.QueryExecution)
            .Classification.Should().Be("permission-denied");
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql, "8.4.11", null)]
    [InlineData(DatabaseReadMySqlFlavor.MySql, "9.1.0", null)]
    [InlineData(DatabaseReadMySqlFlavor.MySql, "5.7.44-log", "provider-version-not-supported")]
    [InlineData(DatabaseReadMySqlFlavor.MySql, "11.4.13-MariaDB-ubu2404", "provider-flavor-mismatch")]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb, "11.4.13-MariaDB-ubu2404", null)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb, "10.11.9-MariaDB", null)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb, "10.6.18-MariaDB", "provider-version-not-supported")]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb, "8.4.11", "provider-flavor-mismatch")]
    public void ServerVersionSelectsTheFlavor(DatabaseReadMySqlFlavor flavor, string version, string? expectedCode)
    {
        var provider = new MySqlDatabaseReadProvider(DatabaseReadMySqlTestServer.For(flavor).Kind);
        var check = () => provider.EnsureSupportedServer(version);

        if (expectedCode is null)
        {
            check.Should().NotThrow();
        }
        else
        {
            var exception = check.Should().Throw<DatabaseReadExpectedException>().Which;
            exception.Code.Should().Be(expectedCode);
            exception.ExitCode.Should().Be(DatabaseReadExitCode.InvalidProfile);
        }
    }

    [Theory]
    [InlineData(null, "SET ROLE NONE;")]
    [InlineData("NONE", "SET ROLE NONE;")]
    [InlineData("`reader_role`@`%`", "SET ROLE `reader_role`@`%`;")]
    [InlineData("`a``b`@`%`,`auditor`@`localhost`", "SET ROLE `a``b`@`%`,`auditor`@`localhost`;")]
    [InlineData("`reader_role`@`%`; SET GLOBAL read_only = 0", "SET ROLE DEFAULT;")]
    public void MySqlRolesAreRestoredOnlyFromTheServersList(string? currentRole, string expected)
    {
        MySqlDatabaseReadProvider.RestoreMySqlRolesSql(currentRole).Should().Be(expected);
    }

    [Theory]
    [InlineData("lower(`name`)", true)]
    [InlineData("concat(`name`,_utf8mb4'`status`')", true)]
    [InlineData("lower(`status`)", false)]
    [InlineData("(`name` + `status`)", false)]
    [InlineData("lower(`name", false)]
    [InlineData("lower(\"status\")", false)]
    public void ExpressionKeysShowOnlySelectableColumns(string expression, bool expected)
    {
        var selectable = new HashSet<string>(["id", "name"], StringComparer.OrdinalIgnoreCase);

        MySqlDatabaseReadProvider.ExpressionColumnsAreSelectable(expression, selectable).Should().Be(expected);
    }

    private static async Task AssertSchemaEvidenceAsync(
        string provider,
        string connectionString,
        string database,
        bool mySql)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);

        using (var search = await RunSchemaAsync(
                   workspace,
                   DatabaseReadTestWorkspace.SchemaRequest("search", database, searchTerm: "projects")))
        {
            var objects = search.RootElement.GetProperty("schema").GetProperty("objects").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
                .ToArray();
            objects.Should().Equal("column_limited_projects", "diagnostic_projects");
        }

        using (var all = await RunSchemaAsync(workspace, DatabaseReadTestWorkspace.SchemaRequest("search")))
        {
            var objects = all.RootElement.GetProperty("schema").GetProperty("objects").EnumerateArray()
                .Select(item => item.GetProperty("schema").GetString())
                .ToArray();
            objects.Should().OnlyContain(schema => schema == database, "system schemas are never searched");
        }

        using var describe = await RunSchemaAsync(
            workspace,
            DatabaseReadTestWorkspace.SchemaRequest("describe", database, "diagnostic_projects"));
        var described = describe.RootElement.GetProperty("schema").GetProperty("object");
        described.GetProperty("sqlIdentifier").GetString().Should().Be($"`{database}`.`diagnostic_projects`");
        described.GetProperty("kind").GetString().Should().Be("table");
        var columns = described.GetProperty("columns").EnumerateArray().ToArray();
        columns.Select(column => column.GetProperty("name").GetString())
            .Should().Equal("id", "tenant_id", "owner_id", "name", "status", "restricted_project_id");
        columns.Single(column => column.GetProperty("name").GetString() == "id")
            .GetProperty("isPrimaryKey").GetBoolean().Should().BeTrue();
        columns.Single(column => column.GetProperty("name").GetString() == "id")
            .GetProperty("providerType").GetString().Should().Be("char(36)");
        columns.Single(column => column.GetProperty("name").GetString() == "restricted_project_id")
            .GetProperty("allowsNull").GetBoolean().Should().BeTrue();

        AssertIndexes(described.GetProperty("indexes").EnumerateArray().ToArray(), mySql);

        var relationships = described.GetProperty("relationships");
        var outgoing = relationships.GetProperty("outgoing").EnumerateArray().ToArray();
        var composite = outgoing.Single(item =>
            item.GetProperty("name").GetString() == "fk_diagnostic_projects_diagnostic_owners");
        composite.GetProperty("isValidated").GetBoolean().Should().BeFalse();
        composite.GetProperty("target").GetProperty("sqlIdentifier").GetString()
            .Should().Be($"`{database}`.`diagnostic_owners`");
        composite.GetProperty("columnPairs").EnumerateArray()
            .Select(pair => $"{pair.GetProperty("position").GetInt32()}:{pair.GetProperty("sourceColumn").GetString()}={pair.GetProperty("targetColumn").GetString()}")
            .Should().Equal("1:tenant_id=tenant_id", "2:owner_id=id");
        outgoing.Should().NotContain(item =>
            item.GetProperty("name").GetString() == "fk_diagnostic_projects_restricted_projects");
        var incoming = relationships.GetProperty("incoming").EnumerateArray().ToArray();
        incoming.Single(item => item.GetProperty("name").GetString() == "fk_diagnostic_tasks_diagnostic_projects")
            .GetProperty("source").GetProperty("sqlIdentifier").GetString()
            .Should().Be($"`{database}`.`diagnostic_tasks`");

        using var indexes = await RunSchemaAsync(
            workspace,
            DatabaseReadTestWorkspace.SchemaRequest("indexes", database, "diagnostic_projects"));
        var indexSet = indexes.RootElement.GetProperty("schema").GetProperty("indexes");
        indexSet.GetProperty("sqlIdentifier").GetString().Should().Be($"`{database}`.`diagnostic_projects`");
        AssertIndexes(indexSet.GetProperty("items").EnumerateArray().ToArray(), mySql);
        indexes.RootElement.GetProperty("schema").GetProperty("evidenceHash").GetString().Should().HaveLength(64);
    }

    private static void AssertIndexes(IReadOnlyList<JsonElement> indexes, bool mySql)
    {
        var primary = indexes[0];
        primary.GetProperty("name").GetString().Should().Be("PRIMARY");
        primary.GetProperty("isPrimaryKey").GetBoolean().Should().BeTrue();
        primary.GetProperty("isUnique").GetBoolean().Should().BeTrue();
        primary.GetProperty("accessMethod").GetString().Should().Be("btree");

        var composite = indexes.Single(index => index.GetProperty("name").GetString() == "ix_diagnostic_projects_name_id");
        composite.GetProperty("isUnique").GetBoolean().Should().BeFalse();
        composite.GetProperty("isPartial").GetBoolean().Should().BeFalse();
        composite.GetProperty("columns").EnumerateArray().Select(column => column.GetString())
            .Should().Equal("name", "id");
        composite.GetProperty("keyColumns").EnumerateArray()
            .Select(column => $"{column.GetProperty("position").GetInt32()}:{column.GetProperty("kind").GetString()}:{column.GetProperty("direction").GetString()}")
            .Should().Equal("1:column:ascending", "2:column:descending");
        composite.GetProperty("includedColumns").GetArrayLength().Should().Be(0);

        indexes.Should().NotContain(index => index.GetProperty("name").GetString() == "ix_diagnostic_projects_hidden");

        if (mySql)
        {
            var expression = indexes.Single(index =>
                index.GetProperty("name").GetString() == "ix_diagnostic_projects_lower_name");
            var key = expression.GetProperty("keyColumns")[0];
            key.GetProperty("kind").GetString().Should().Be("expression");
            key.GetProperty("expression").GetString()!.ToLowerInvariant().Should().Contain("lower");
            expression.GetProperty("columns").GetArrayLength().Should().Be(0);
        }
    }

    private static async Task AssertColumnLimitedEvidenceAsync(
        string provider,
        string connectionString,
        string database,
        bool mySql)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using var describe = await RunSchemaAsync(
            workspace,
            DatabaseReadTestWorkspace.SchemaRequest("describe", database, "column_limited_projects"));
        var described = describe.RootElement.GetProperty("schema").GetProperty("object");
        described.GetProperty("columns").EnumerateArray()
            .Select(column => column.GetProperty("name").GetString())
            .Should().Equal("id", "name");
        var names = described.GetProperty("indexes").EnumerateArray()
            .Select(index => index.GetProperty("name").GetString())
            .ToArray();
        names.Should().Contain("ix_column_limited_projects_name");
        names.Should().NotContain("ix_column_limited_projects_status");
        names.Should().NotContain("ix_column_limited_projects_lower_status");
        if (mySql)
        {
            names.Should().Contain("ix_column_limited_projects_lower_name");
        }
    }

    private static async Task AssertSchemaFailureAsync(
        string provider,
        string connectionString,
        MemoryStream request,
        string expectedCode)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using (request)
        using (var output = new MemoryStream())
        {
            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["schema", "--profiles", workspace.ProfileCatalogPath],
                request,
                output);

            exitCode.Should().Be(5, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
            using var response = DatabaseReadTestWorkspace.ParseOutput(output);
            response.RootElement.TryGetProperty("schema", out _).Should().BeFalse();
            response.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(expectedCode);
        }
    }

    private static async Task AssertClassifiedFailureAsync(
        string provider,
        string connectionString,
        string sql,
        string classification,
        string providerCode)
    {
        using var response = await RunQueryAsync(provider, connectionString, sql, 5);
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("database-query-failed");
        error.GetProperty("classification").GetString().Should().Be(classification);
        error.GetProperty("provider").GetString().Should().Be(provider);
        error.GetProperty("providerCode").GetString().Should().Be(providerCode);
        error.GetProperty("stage").GetString().Should().Be("query-execution");
        response.RootElement.GetRawText().Should().NotContain(ReaderPassword);
    }

    private static async Task<JsonDocument> RunSchemaAsync(DatabaseReadTestWorkspace workspace, MemoryStream request)
    {
        using (request)
        using (var output = new MemoryStream())
        {
            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["schema", "--profiles", workspace.ProfileCatalogPath],
                request,
                output);

            exitCode.Should().Be(0, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
            return DatabaseReadTestWorkspace.ParseOutput(output);
        }
    }

    private static async Task<JsonDocument> RunQueryAsync(
        string provider,
        string connectionString,
        string sql,
        int expectedExitCode,
        int maximumRows = 10)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, connectionString);
        using var input = DatabaseReadTestWorkspace.Request(sql, maximumRows: maximumRows);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(expectedExitCode, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
        return DatabaseReadTestWorkspace.ParseOutput(output);
    }
}
