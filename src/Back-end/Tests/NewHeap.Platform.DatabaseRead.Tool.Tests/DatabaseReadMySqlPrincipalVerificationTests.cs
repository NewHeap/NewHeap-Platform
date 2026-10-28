using AwesomeAssertions;
using MySqlConnector;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// MySQL and MariaDB principal verification on real servers: a principal that can only select
/// passes, and each grant of another privilege kind, directly or through a role, is rejected with
/// its own verification check. Each scenario grants one thing to the principal that passed,
/// verifies, and takes it back, so a rejection always comes from that grant.
/// </summary>
/// <remarks>
/// Accounts and roles belong to the server that the test process shares, so their names start
/// with the name of the test's own database, and the test drops them when it ends. Grants that
/// reach every account, mandatory roles and <c>PUBLIC</c>, are proved in
/// <see cref="DatabaseReadMySqlServerWideGrantTests"/>.
/// </remarks>
public sealed class DatabaseReadMySqlPrincipalVerificationTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

    private static readonly DatabaseReadLimits Limits = new(10, 10, 2_000, 65_536, 4_096, 8_192);

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task RejectsEveryPrivilegeKindAndAcceptsASelectOnlyPrincipal(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var admin = new MySqlConnectionStringBuilder(await server.CreateDatabaseAsync("pv"));
        var database = admin.Database;
        var reader = $"`{database}_reader`@`%`";
        var target = $"`{database}_target`@`%`";
        var writer = $"`{database}_writer`";
        var outer = $"`{database}_outer`";
        var inner = $"`{database}_inner`";
        var plain = $"`{database}_plain`";
        var selector = $"`{database}_selector`";
        await DatabaseReadMySqlTestServer.ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE items (id int NOT NULL PRIMARY KEY, name varchar(100) NOT NULL);
            CREATE TABLE other_items (id int NOT NULL PRIMARY KEY, name varchar(100) NOT NULL);
            CREATE SQL SECURITY INVOKER VIEW item_names AS SELECT id, name FROM items;
            CREATE FUNCTION item_count() RETURNS int READS SQL DATA RETURN (SELECT COUNT(*) FROM items);
            CREATE USER {reader} IDENTIFIED BY '{ReaderPassword}';
            CREATE USER {target} IDENTIFIED BY '{ReaderPassword}';
            CREATE ROLE {writer};
            CREATE ROLE {outer};
            CREATE ROLE {inner};
            CREATE ROLE {plain};
            CREATE ROLE {selector};
            GRANT DELETE ON `{database}`.items TO {writer};
            GRANT DELETE ON `{database}`.other_items TO {inner};
            GRANT {inner} TO {outer};
            GRANT SELECT ON `{database}`.other_items TO {selector};
            GRANT SELECT ON `{database}`.items TO {reader};
            GRANT SELECT (id) ON `{database}`.other_items TO {reader};
            GRANT SELECT ON `{database}`.item_names TO {reader};
            """);
        var readerConnectionString = DatabaseReadMySqlTestServer.AccountConnectionString(
            admin.ConnectionString,
            database + "_reader",
            ReaderPassword);
        var serverPrivilege = flavor == DatabaseReadMySqlFlavor.MySql
            ? new PrincipalScenario(
                "dynamic BACKUP_ADMIN",
                $"GRANT BACKUP_ADMIN ON *.* TO {reader}",
                $"REVOKE BACKUP_ADMIN ON *.* FROM {reader}",
                "dynamic-privilege")
            : new PrincipalScenario(
                "BINLOG MONITOR",
                $"GRANT BINLOG MONITOR ON *.* TO {reader}",
                $"REVOKE BINLOG MONITOR ON *.* FROM {reader}",
                "global-privilege");
        var scenarios = new[]
        {
            new PrincipalScenario("global SELECT", $"GRANT SELECT ON *.* TO {reader}",
                $"REVOKE SELECT ON *.* FROM {reader}", "global-privilege"),
            new PrincipalScenario("global PROCESS", $"GRANT PROCESS ON *.* TO {reader}",
                $"REVOKE PROCESS ON *.* FROM {reader}", "global-privilege"),
            serverPrivilege,
            new PrincipalScenario("PROXY", $"GRANT PROXY ON {target} TO {reader}",
                $"REVOKE PROXY ON {target} FROM {reader}", "proxy-privilege"),
            new PrincipalScenario("SELECT with GRANT OPTION",
                $"GRANT SELECT ON `{database}`.other_items TO {reader} WITH GRANT OPTION",
                $"REVOKE SELECT, GRANT OPTION ON `{database}`.other_items FROM {reader}", "grant-option"),
            new PrincipalScenario("role with ADMIN OPTION", $"GRANT {plain} TO {reader} WITH ADMIN OPTION",
                $"REVOKE {plain} FROM {reader}", "role-admin-option"),
            new PrincipalScenario("SELECT on a table of the mysql schema", $"GRANT SELECT ON mysql.db TO {reader}",
                $"REVOKE SELECT ON mysql.db FROM {reader}", "system-schema-select"),
            new PrincipalScenario("SELECT on a schema pattern that matches mysql",
                $"GRANT SELECT ON `my_ql`.* TO {reader}",
                $"REVOKE SELECT ON `my_ql`.* FROM {reader}", "system-schema-select"),
            new PrincipalScenario("schema-level INSERT", $"GRANT INSERT ON `{database}`.* TO {reader}",
                $"REVOKE INSERT ON `{database}`.* FROM {reader}", "schema-privilege-not-allowlisted"),
            new PrincipalScenario("schema-level CREATE TEMPORARY TABLES",
                $"GRANT CREATE TEMPORARY TABLES ON `{database}`.* TO {reader}",
                $"REVOKE CREATE TEMPORARY TABLES ON `{database}`.* FROM {reader}", "schema-privilege-not-allowlisted"),
            new PrincipalScenario("schema-level SHOW VIEW", $"GRANT SHOW VIEW ON `{database}`.* TO {reader}",
                $"REVOKE SHOW VIEW ON `{database}`.* FROM {reader}", "schema-privilege-not-allowlisted"),
            new PrincipalScenario("UPDATE on a table", $"GRANT UPDATE ON `{database}`.items TO {reader}",
                $"REVOKE UPDATE ON `{database}`.items FROM {reader}", "table-privilege-not-allowlisted"),
            new PrincipalScenario("column-level UPDATE", $"GRANT UPDATE (name) ON `{database}`.other_items TO {reader}",
                $"REVOKE UPDATE (name) ON `{database}`.other_items FROM {reader}", "column-privilege-not-allowlisted"),
            new PrincipalScenario("EXECUTE on a function",
                $"GRANT EXECUTE ON FUNCTION `{database}`.item_count TO {reader}",
                $"REVOKE EXECUTE ON FUNCTION `{database}`.item_count FROM {reader}", "routine-privilege"),
            new PrincipalScenario("DELETE through a role", $"GRANT {writer} TO {reader}",
                $"REVOKE {writer} FROM {reader}", "table-privilege-not-allowlisted"),
            new PrincipalScenario("DELETE through a nested role", $"GRANT {outer} TO {reader}",
                $"REVOKE {outer} FROM {reader}", "table-privilege-not-allowlisted"),
            new PrincipalScenario("SELECT on a view that runs as its definer",
                $"CREATE SQL SECURITY DEFINER VIEW `{database}`.definer_names AS SELECT id, name FROM `{database}`.items; " +
                $"GRANT SELECT ON `{database}`.definer_names TO {reader}",
                $"DROP VIEW `{database}`.definer_names", "definer-view"),
            new PrincipalScenario("SELECT on a view that runs as its definer through a role",
                $"CREATE SQL SECURITY DEFINER VIEW `{database}`.definer_role_names AS SELECT id FROM `{database}`.items; " +
                $"GRANT SELECT ON `{database}`.definer_role_names TO {plain}; GRANT {plain} TO {reader}",
                $"REVOKE {plain} FROM {reader}; DROP VIEW `{database}`.definer_role_names", "definer-view"),
            new PrincipalScenario("a role that only selects", $"GRANT {selector} TO {reader}",
                $"REVOKE {selector} FROM {reader}", null),
            new PrincipalScenario("schema-level SELECT", $"GRANT SELECT ON `{database}`.* TO {reader}",
                $"REVOKE SELECT ON `{database}`.* FROM {reader}", null)
        };
        var provider = new MySqlDatabaseReadProvider(server.Kind);

        try
        {
            (await VerifyAsync(provider, readerConnectionString))
                .Should().Be(new DatabaseReadPrincipalVerification(true), "the SELECT-only principal must pass");

            var mismatches = new List<string>();
            foreach (var scenario in scenarios)
            {
                await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, scenario.Grant);
                var verification = await VerifyAsync(provider, readerConnectionString);
                await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, scenario.Revoke);

                var expected = scenario.ExpectedCheck is null
                    ? new DatabaseReadPrincipalVerification(true)
                    : new DatabaseReadPrincipalVerification(false, scenario.ExpectedCheck);
                if (verification != expected)
                {
                    mismatches.Add($"{scenario.Name}: expected {Describe(expected)}, verified {Describe(verification)}");
                }

                (await VerifyAsync(provider, readerConnectionString))
                    .Should().Be(
                        new DatabaseReadPrincipalVerification(true),
                        "the principal must pass again after the grant for {0} was taken back",
                        scenario.Name);
            }

            string.Join(Environment.NewLine, mismatches).Should().BeEmpty();

            await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, $"GRANT {selector} TO {reader}");
            await AssertRolesAreRestoredAsync(provider, readerConnectionString, flavor);
            await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, $"REVOKE {selector} FROM {reader}");

            await DatabaseReadMySqlTestServer.ExecuteAsync(
                admin.ConnectionString,
                $"GRANT UPDATE ON `{database}`.items TO {reader}");
            await AssertRejectedThroughTheToolAsync(
                server.ProviderName,
                readerConnectionString,
                "table-privilege-not-allowlisted",
                database + "_reader");
        }
        finally
        {
            await DatabaseReadMySqlTestServer.ExecuteAsync(
                admin.ConnectionString,
                $"DROP USER IF EXISTS {reader}, {target}; DROP ROLE IF EXISTS {writer}, {outer}, {inner}, {plain}, {selector};");
        }
    }

    /// <summary>
    /// The verification activates roles to read their grants; afterwards the session has the roles
    /// it had before, none here, so the diagnostic runs with the principal's own active roles.
    /// </summary>
    private static async Task AssertRolesAreRestoredAsync(
        MySqlDatabaseReadProvider provider,
        string connectionString,
        DatabaseReadMySqlFlavor flavor)
    {
        await using var connection = provider.CreateConnection(connectionString, Guid.NewGuid().ToString("N"), Limits);
        await connection.OpenAsync();
        (await provider.VerifyReadOnlyPrincipalAsync(connection, Limits, CancellationToken.None))
            .IsReadOnly.Should().BeTrue();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CURRENT_ROLE()";
        var currentRole = await command.ExecuteScalarAsync();

        if (flavor == DatabaseReadMySqlFlavor.MySql)
        {
            currentRole.Should().Be("NONE");
        }
        else
        {
            currentRole.Should().Be(DBNull.Value);
        }
    }

    private static async Task AssertRejectedThroughTheToolAsync(
        string provider,
        string readerConnectionString,
        string expectedCheck,
        string principalName)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, readerConnectionString);
        foreach (var command in new[] { "schema", "query" })
        {
            using var input = command == "schema"
                ? DatabaseReadTestWorkspace.SchemaRequest("search")
                : DatabaseReadTestWorkspace.Request("SELECT id FROM items LIMIT 1");
            using var output = new MemoryStream();

            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                [command, "--profiles", workspace.ProfileCatalogPath],
                input,
                output);

            exitCode.Should().Be(4, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
            using var response = DatabaseReadTestWorkspace.ParseOutput(output);
            var error = response.RootElement.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("read-only-principal-not-verified");
            error.GetProperty("verificationCheck").GetString().Should().Be(expectedCheck);
            error.GetProperty("stage").GetString().Should().Be("readonly-verification");
            response.RootElement.TryGetProperty("result", out _).Should().BeFalse();
            response.RootElement.TryGetProperty("schema", out _).Should().BeFalse();
            response.RootElement.GetRawText().Should().NotContain(ReaderPassword).And.NotContain(principalName);
        }
    }

    internal static async Task<DatabaseReadPrincipalVerification> VerifyAsync(
        IDatabaseReadProvider provider,
        string connectionString)
    {
        await using var connection = provider.CreateConnection(connectionString, Guid.NewGuid().ToString("N"), Limits);
        await connection.OpenAsync();

        return await provider.VerifyReadOnlyPrincipalAsync(connection, Limits, CancellationToken.None);
    }

    private static string Describe(DatabaseReadPrincipalVerification verification)
    {
        return verification.IsReadOnly ? "read-only" : $"rejected by {verification.FailedCheck ?? "(no check)"}";
    }

    private sealed record PrincipalScenario(string Name, string Grant, string Revoke, string? ExpectedCheck);
}
