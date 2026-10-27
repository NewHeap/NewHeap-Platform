using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// Read-only principal verification on real servers: a principal that can only connect and select
/// passes, and each grant that adds a write, DDL, cross-session or administrative capability is
/// rejected with its own verification check. Each scenario grants one capability to the principal
/// that passed, verifies, and takes the grant back, so a rejection always comes from that grant.
/// </summary>
/// <remarks>
/// Roles and logins belong to the server that the test process shares, so their names start with
/// the name of the test's own database, and the test drops them when it ends. The reader connects
/// without pooling, so no idle session of it remains when it is dropped.
/// </remarks>
public sealed class DatabaseReadPrincipalVerificationTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

    private static readonly DatabaseReadLimits Limits = new(10, 10, 2_000, 65_536, 4_096, 8_192);

    [Fact]
    public async Task PostgreSqlRejectsEveryElevatedGrantAndAcceptsASelectOnlyPrincipal()
    {
        var admin = new NpgsqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.PostgreSql).CreateDatabaseAsync("principals"));
        var reader = admin.Database + "_reader";
        var group = admin.Database + "_group";
        var administrator = admin.Database + "_administrator";
        await ExecutePostgreSqlAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE items (id integer NOT NULL PRIMARY KEY, name text NOT NULL);
            CREATE TABLE other_items (id integer NOT NULL PRIMARY KEY, name text NOT NULL);
            CREATE VIEW item_names AS SELECT id, name FROM items;
            CREATE SEQUENCE item_numbers;
            CREATE ROLE {reader} LOGIN PASSWORD '{ReaderPassword}';
            CREATE ROLE {group} NOLOGIN;
            CREATE ROLE {administrator} NOLOGIN CREATEROLE;
            GRANT CONNECT ON DATABASE "{admin.Database}" TO {reader};
            GRANT USAGE ON SCHEMA public TO {reader};
            GRANT SELECT ON TABLE items, item_names TO {reader};
            GRANT SELECT (id) ON TABLE other_items TO {reader};
            GRANT SELECT ON SEQUENCE item_numbers TO {reader};
            """);
        var readerConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Username = reader,
            Password = ReaderPassword,
            Pooling = false
        }.ConnectionString;
        var scenarios = new PrincipalScenario[]
        {
            new("pg_monitor", $"GRANT pg_monitor TO {reader}", $"REVOKE pg_monitor FROM {reader}",
                "predefined-role-membership"),
            new("pg_read_all_stats", $"GRANT pg_read_all_stats TO {reader}",
                $"REVOKE pg_read_all_stats FROM {reader}", "predefined-role-membership"),
            new("pg_read_all_settings", $"GRANT pg_read_all_settings TO {reader}",
                $"REVOKE pg_read_all_settings FROM {reader}", "predefined-role-membership"),
            new("pg_signal_backend", $"GRANT pg_signal_backend TO {reader}",
                $"REVOKE pg_signal_backend FROM {reader}", "predefined-role-membership"),
            new("pg_read_server_files", $"GRANT pg_read_server_files TO {reader}",
                $"REVOKE pg_read_server_files FROM {reader}", "predefined-role-membership"),
            new("pg_write_server_files", $"GRANT pg_write_server_files TO {reader}",
                $"REVOKE pg_write_server_files FROM {reader}", "predefined-role-membership"),
            new("pg_execute_server_program", $"GRANT pg_execute_server_program TO {reader}",
                $"REVOKE pg_execute_server_program FROM {reader}", "predefined-role-membership"),
            new("pg_monitor inherited through a group role",
                $"GRANT pg_monitor TO {group}; GRANT {group} TO {reader}",
                $"REVOKE {group} FROM {reader}; REVOKE pg_monitor FROM {group}",
                "predefined-role-membership"),
            new("membership without inheritance in a role that can create roles",
                $"GRANT {administrator} TO {reader} WITH INHERIT FALSE",
                $"REVOKE {administrator} FROM {reader}", "role-attribute-createrole"),
            new("EXECUTE on pg_stat_reset", $"GRANT EXECUTE ON FUNCTION pg_catalog.pg_stat_reset() TO {reader}",
                $"REVOKE EXECUTE ON FUNCTION pg_catalog.pg_stat_reset() FROM {reader}", "catalog-function-execute"),
            new("EXECUTE on pg_ls_logdir granted to PUBLIC",
                "GRANT EXECUTE ON FUNCTION pg_catalog.pg_ls_logdir() TO PUBLIC",
                "REVOKE EXECUTE ON FUNCTION pg_catalog.pg_ls_logdir() FROM PUBLIC", "catalog-function-execute"),
            new("function in a schema named pgx",
                "CREATE SCHEMA pgx; CREATE FUNCTION pgx.probe() RETURNS integer LANGUAGE sql AS 'SELECT 1'; " +
                $"GRANT USAGE ON SCHEMA pgx TO {reader}",
                "DROP SCHEMA pgx CASCADE", "function-execute"),
            new("column-level UPDATE", $"GRANT UPDATE (name) ON TABLE other_items TO {reader}",
                $"REVOKE UPDATE (name) ON TABLE other_items FROM {reader}", "column-write"),
            new("INSERT through a view", $"GRANT INSERT ON TABLE item_names TO {reader}",
                $"REVOKE INSERT ON TABLE item_names FROM {reader}", "relation-write"),
            new("UPDATE on a table", $"GRANT UPDATE ON TABLE items TO {reader}",
                $"REVOKE UPDATE ON TABLE items FROM {reader}", "relation-write"),
            new("USAGE on a sequence", $"GRANT USAGE ON SEQUENCE item_numbers TO {reader}",
                $"REVOKE USAGE ON SEQUENCE item_numbers FROM {reader}", "sequence-usage"),
            new("ownership of a table", $"ALTER TABLE other_items OWNER TO {reader}",
                $"ALTER TABLE other_items OWNER TO {admin.Username}", "object-owner"),
            new("pg_read_all_data", $"GRANT pg_read_all_data TO {reader}",
                $"REVOKE pg_read_all_data FROM {reader}", null)
        };

        try
        {
            await AssertScenariosAsync(
                new PostgreSqlDatabaseReadProvider(),
                readerConnectionString,
                scenarios,
                (sql, _) => ExecutePostgreSqlAsync(admin.ConnectionString, sql));

            await ExecutePostgreSqlAsync(admin.ConnectionString, $"GRANT pg_monitor TO {reader}");
            await AssertRejectedThroughTheToolAsync(
                "postgresql",
                readerConnectionString,
                "SELECT id FROM items LIMIT 1",
                "predefined-role-membership",
                reader);
        }
        finally
        {
            await ExecutePostgreSqlAsync(
                admin.ConnectionString,
                $"""
                DROP SCHEMA IF EXISTS pgx CASCADE;
                DROP OWNED BY {reader}, {group}, {administrator};
                DROP ROLE {reader};
                DROP ROLE {group};
                DROP ROLE {administrator};
                """);
        }
    }

    [Fact]
    public async Task SqlServerRejectsEveryElevatedGrantAndAcceptsASelectOnlyPrincipal()
    {
        var admin = new SqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.SqlServer).CreateDatabaseAsync("principals"));
        var master = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = "master" };
        var reader = admin.InitialCatalog + "_reader";
        var target = admin.InitialCatalog + "_target";
        var role = admin.InitialCatalog + "_role";
        var passwordKeyword = "PASS" + "WORD";
        await ExecuteSqlServerAsync(
            master.ConnectionString,
            $"""
            CREATE LOGIN [{reader}] WITH {passwordKeyword} = '{ReaderPassword}';
            CREATE LOGIN [{target}] WITH {passwordKeyword} = '{ReaderPassword}';
            """);
        await ExecuteSqlServerAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE dbo.Items (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
            CREATE TABLE dbo.OtherItems (Id int NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL);
            CREATE SEQUENCE dbo.ItemNumbers AS int START WITH 1;
            CREATE USER [{reader}] FOR LOGIN [{reader}];
            CREATE USER [{target}] FOR LOGIN [{target}];
            CREATE ROLE [{role}];
            GRANT SELECT ON dbo.Items TO [{reader}];
            GRANT SELECT (Id) ON dbo.OtherItems TO [{reader}];
            GRANT VIEW DEFINITION ON OBJECT::dbo.Items TO [{reader}];
            """);
        var readerConnectionString = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = reader,
            Password = ReaderPassword,
            IntegratedSecurity = false,
            Pooling = false
        }.ConnectionString;
        var scenarios = new PrincipalScenario[]
        {
            new("VIEW SERVER STATE", $"GRANT VIEW SERVER STATE TO [{reader}]",
                $"REVOKE VIEW SERVER STATE FROM [{reader}]", "server-permission-view-server-state", ServerScope: true),
            new("server state reader role", $"ALTER SERVER ROLE [##MS_ServerStateReader##] ADD MEMBER [{reader}]",
                $"ALTER SERVER ROLE [##MS_ServerStateReader##] DROP MEMBER [{reader}]",
                "server-permission-view-server-state", ServerScope: true),
            new("ALTER ANY CONNECTION", $"GRANT ALTER ANY CONNECTION TO [{reader}]",
                $"REVOKE ALTER ANY CONNECTION FROM [{reader}]", "server-permission-alter-any-connection",
                ServerScope: true),
            new("IMPERSONATE ANY LOGIN", $"GRANT IMPERSONATE ANY LOGIN TO [{reader}]",
                $"REVOKE IMPERSONATE ANY LOGIN FROM [{reader}]", "server-permission-impersonate-any-login",
                ServerScope: true),
            new("IMPERSONATE on a login", $"GRANT IMPERSONATE ON LOGIN::[{target}] TO [{reader}]",
                $"REVOKE IMPERSONATE ON LOGIN::[{target}] FROM [{reader}]", "login-impersonate", ServerScope: true),
            new("IMPERSONATE on a user", $"GRANT IMPERSONATE ON USER::[{target}] TO [{reader}]",
                $"REVOKE IMPERSONATE ON USER::[{target}] FROM [{reader}]", "user-impersonate"),
            new("ALTER ANY SCHEMA", $"GRANT ALTER ANY SCHEMA TO [{reader}]",
                $"REVOKE ALTER ANY SCHEMA FROM [{reader}]", "database-permission-alter-any-schema"),
            new("UPDATE on a sequence", $"GRANT UPDATE ON OBJECT::dbo.ItemNumbers TO [{reader}]",
                $"REVOKE UPDATE ON OBJECT::dbo.ItemNumbers FROM [{reader}]", "sequence-update"),
            new("ALTER on a schema", $"GRANT ALTER ON SCHEMA::dbo TO [{reader}]",
                $"REVOKE ALTER ON SCHEMA::dbo FROM [{reader}]", "schema-permission-alter"),
            new("TAKE OWNERSHIP on a schema", $"GRANT TAKE OWNERSHIP ON SCHEMA::dbo TO [{reader}]",
                $"REVOKE TAKE OWNERSHIP ON SCHEMA::dbo FROM [{reader}]", "schema-permission-take-ownership"),
            new("ALTER on a table", $"GRANT ALTER ON OBJECT::dbo.Items TO [{reader}]",
                $"REVOKE ALTER ON OBJECT::dbo.Items FROM [{reader}]", "object-permission-alter"),
            new("TAKE OWNERSHIP on a table", $"GRANT TAKE OWNERSHIP ON OBJECT::dbo.Items TO [{reader}]",
                $"REVOKE TAKE OWNERSHIP ON OBJECT::dbo.Items FROM [{reader}]", "object-permission-take-ownership"),
            new("column-level UPDATE", $"GRANT UPDATE (Name) ON dbo.OtherItems TO [{reader}]",
                $"REVOKE UPDATE (Name) ON dbo.OtherItems FROM [{reader}]", "column-update"),
            new("VIEW DATABASE STATE", $"GRANT VIEW DATABASE STATE TO [{reader}]",
                $"REVOKE VIEW DATABASE STATE FROM [{reader}]", "database-permission-not-allowlisted"),
            new("REFERENCES on a table", $"GRANT REFERENCES ON OBJECT::dbo.Items TO [{reader}]",
                $"REVOKE REFERENCES ON OBJECT::dbo.Items FROM [{reader}]", "object-permission-not-allowlisted"),
            new("ALTER on a role", $"GRANT ALTER ON ROLE::[{role}] TO [{reader}]",
                $"REVOKE ALTER ON ROLE::[{role}] FROM [{reader}]", "database-principal-permission-not-allowlisted"),
            new("ALTER on a login", $"GRANT ALTER ON LOGIN::[{target}] TO [{reader}]",
                $"REVOKE ALTER ON LOGIN::[{target}] FROM [{reader}]", "server-principal-permission-not-allowlisted",
                ServerScope: true),
            new("db_datareader", $"ALTER ROLE db_datareader ADD MEMBER [{reader}]",
                $"ALTER ROLE db_datareader DROP MEMBER [{reader}]", null),
            new("database-level SELECT, VIEW DEFINITION and SHOWPLAN",
                $"GRANT SELECT, VIEW DEFINITION, SHOWPLAN TO [{reader}]",
                $"REVOKE SELECT, VIEW DEFINITION, SHOWPLAN FROM [{reader}]", null)
        };

        try
        {
            await AssertScenariosAsync(
                new SqlServerDatabaseReadProvider(),
                readerConnectionString,
                scenarios,
                (sql, serverScope) => ExecuteSqlServerAsync(
                    serverScope ? master.ConnectionString : admin.ConnectionString,
                    sql));

            await ExecuteSqlServerAsync(
                admin.ConnectionString,
                $"GRANT UPDATE ON OBJECT::dbo.ItemNumbers TO [{reader}]");
            await AssertRejectedThroughTheToolAsync(
                "sql-server",
                readerConnectionString,
                "SELECT TOP (1) Id FROM dbo.Items",
                "sequence-update",
                reader);
        }
        finally
        {
            await ExecuteSqlServerAsync(
                admin.ConnectionString,
                $"""
                DROP USER [{reader}];
                DROP USER [{target}];
                DROP ROLE [{role}];
                """);
            await ExecuteSqlServerAsync(
                master.ConnectionString,
                $"""
                DROP LOGIN [{reader}];
                DROP LOGIN [{target}];
                """);
        }
    }

    private static async Task AssertScenariosAsync(
        IDatabaseReadProvider provider,
        string readerConnectionString,
        IReadOnlyList<PrincipalScenario> scenarios,
        Func<string, bool, Task> execute)
    {
        (await VerifyAsync(provider, readerConnectionString))
            .Should().Be(new DatabaseReadPrincipalVerification(true), "the SELECT-only principal must pass");

        var mismatches = new List<string>();
        foreach (var scenario in scenarios)
        {
            await execute(scenario.Grant, scenario.ServerScope);
            var verification = await VerifyAsync(provider, readerConnectionString);
            await execute(scenario.Revoke, scenario.ServerScope);

            var expected = scenario.ExpectedCheck is null
                ? new DatabaseReadPrincipalVerification(true)
                : new DatabaseReadPrincipalVerification(false, scenario.ExpectedCheck);
            if (verification != expected)
            {
                mismatches.Add(
                    $"{scenario.Name}: expected {Describe(expected)}, verified {Describe(verification)}");
            }

            (await VerifyAsync(provider, readerConnectionString))
                .Should().Be(
                    new DatabaseReadPrincipalVerification(true),
                    "the principal must pass again after the grant for {0} was taken back",
                    scenario.Name);
        }

        string.Join(Environment.NewLine, mismatches).Should().BeEmpty();
    }

    private static async Task<DatabaseReadPrincipalVerification> VerifyAsync(
        IDatabaseReadProvider provider,
        string connectionString)
    {
        await using var connection = provider.CreateConnection(
            connectionString,
            Guid.NewGuid().ToString("N"),
            Limits);
        await connection.OpenAsync();

        return await provider.VerifyReadOnlyPrincipalAsync(connection, Limits, CancellationToken.None);
    }

    private static async Task AssertRejectedThroughTheToolAsync(
        string provider,
        string readerConnectionString,
        string sql,
        string expectedCheck,
        string principalName)
    {
        using var workspace = new DatabaseReadTestWorkspace(provider, readerConnectionString);
        foreach (var command in new[] { "schema", "query" })
        {
            using var input = command == "schema"
                ? DatabaseReadTestWorkspace.SchemaRequest("search")
                : DatabaseReadTestWorkspace.Request(sql);
            using var output = new MemoryStream();

            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                [command, "--profiles", workspace.ProfileCatalogPath],
                input,
                output);

            exitCode.Should().Be(4);
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

    private static string Describe(DatabaseReadPrincipalVerification verification)
    {
        return verification.IsReadOnly ? "read-only" : $"rejected by {verification.FailedCheck ?? "(no check)"}";
    }

    private static async Task ExecutePostgreSqlAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteSqlServerAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <param name="ServerScope">The statements change the server, which SQL Server accepts only
    /// in <c>master</c>.</param>
    private sealed record PrincipalScenario(
        string Name,
        string Grant,
        string Revoke,
        string? ExpectedCheck,
        bool ServerScope = false);
}
