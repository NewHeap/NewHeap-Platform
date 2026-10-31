using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Globalization;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A database of its own for one test on one of the four providers, with the same objects on each:
/// <c>session_items</c> and <c>session_owners</c> joined by a foreign key, <c>session_wide</c> with too
/// many columns for the smallest output limit, <c>session_totals</c> in a second schema, and
/// <c>session_restricted</c>, which the SELECT-only principal cannot read. What differs per provider,
/// such as how to find the connection or end it, is a member of this class, so one test body proves a
/// behavior on all of them.
/// </summary>
internal sealed class DatabaseReadSessionDatabase : IAsyncDisposable
{
    private const string Password = "Test-only-session-Password-42";

    private static readonly string WideColumns = string.Join(
        ", ",
        Enumerable.Range(1, 60).Select(index => $"wide_column_{index:D2} int NOT NULL"));

    private Func<Task> _cleanup = () => Task.CompletedTask;

    private DatabaseReadSessionDatabase()
    {
    }

    public required string ProviderName { get; init; }

    public required string ReaderConnectionString { get; init; }

    public required string Schema { get; init; }

    public required string OtherSchema { get; init; }

    /// <summary>
    /// A statement that returns the server's identifier of the connection it runs on.
    /// </summary>
    public required string ConnectionIdSql { get; init; }

    /// <summary>
    /// A statement that runs longer than the shortest timeout, without any function that the
    /// read-only policy refuses.
    /// </summary>
    public required string SlowSql { get; init; }

    /// <summary>
    /// A statement that returns the session state a request depends on, in the order
    /// <see cref="AssertRequestState"/> reads it.
    /// </summary>
    public required string StateSql { get; init; }

    public required Action<JsonElement, int> AssertRequestState { get; init; }

    /// <summary>
    /// The verification check that <see cref="GrantWriteAsync"/> makes the principal fail.
    /// </summary>
    public required string WriteCheck { get; init; }

    public required Func<Task> GrantWriteAsync { get; init; }

    public required Func<Task> RevokeWriteAsync { get; init; }

    public required Func<string, Task> EndConnectionAsync { get; init; }

    public static Task<DatabaseReadSessionDatabase> CreateAsync(RelationalTestProvider provider)
    {
        return provider == RelationalTestProvider.SqlServer
            ? CreateSqlServerAsync()
            : CreatePostgreSqlAsync();
    }

    public static async Task<DatabaseReadSessionDatabase> CreateAsync(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var mySql = flavor == DatabaseReadMySqlFlavor.MySql;
        var admin = new MySqlConnectionStringBuilder(await server.CreateDatabaseAsync("session"));
        var other = new MySqlConnectionStringBuilder(await server.CreateDatabaseAsync("sessionother"));
        var reader = admin.Database + "_reader";
        await DatabaseReadMySqlTestServer.ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE session_owners (id int NOT NULL PRIMARY KEY, name varchar(100) NOT NULL);
            CREATE TABLE session_items (
                id int NOT NULL PRIMARY KEY,
                owner_id int NOT NULL,
                name varchar(100) NOT NULL,
                KEY ix_session_items_name (name),
                CONSTRAINT fk_session_items_session_owners FOREIGN KEY (owner_id) REFERENCES session_owners (id)
            );
            CREATE TABLE session_restricted (id int NOT NULL PRIMARY KEY);
            CREATE TABLE session_wide ({WideColumns});
            CREATE TABLE session_digits (d int NOT NULL PRIMARY KEY);
            INSERT INTO session_digits (d) VALUES (0), (1), (2), (3), (4), (5), (6), (7), (8), (9);
            CREATE TABLE session_numbers (n int NOT NULL PRIMARY KEY);
            INSERT INTO session_numbers (n)
            SELECT hundreds.d * 100 + tens.d * 10 + ones.d
            FROM session_digits AS hundreds CROSS JOIN session_digits AS tens CROSS JOIN session_digits AS ones;
            INSERT INTO session_owners (id, name) VALUES (1, 'Owner');
            INSERT INTO session_items (id, owner_id, name) VALUES (1, 1, 'One'), (2, 1, 'Two'), (3, 1, 'Three');
            CREATE TABLE `{other.Database}`.session_totals (owner_id int NOT NULL, total int NOT NULL);
            CREATE USER `{reader}`@`%` IDENTIFIED BY '{Password}';
            GRANT SELECT ON `{admin.Database}`.session_owners TO `{reader}`@`%`;
            GRANT SELECT ON `{admin.Database}`.session_items TO `{reader}`@`%`;
            GRANT SELECT ON `{admin.Database}`.session_wide TO `{reader}`@`%`;
            GRANT SELECT ON `{admin.Database}`.session_numbers TO `{reader}`@`%`;
            GRANT SELECT ON `{other.Database}`.session_totals TO `{reader}`@`%`;
            """);

        Task ExecuteAsync(string sql) => DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, sql);

        return new DatabaseReadSessionDatabase
        {
            ProviderName = server.ProviderName,
            ReaderConnectionString = DatabaseReadMySqlTestServer.AccountConnectionString(
                admin.ConnectionString,
                reader,
                Password),
            Schema = admin.Database,
            OtherSchema = other.Database,
            ConnectionIdSql = "SELECT CONNECTION_ID()",
            SlowSql =
                "SELECT SUM(a.n + b.n + c.n) FROM session_numbers AS a " +
                "CROSS JOIN session_numbers AS b CROSS JOIN session_numbers AS c",
            StateSql = mySql
                ? "SELECT @@SESSION.max_execution_time, @@SESSION.innodb_lock_wait_timeout, @@SESSION.lock_wait_timeout"
                : "SELECT @@SESSION.max_statement_time, @@SESSION.innodb_lock_wait_timeout, @@SESSION.lock_wait_timeout",
            AssertRequestState = (row, timeoutSeconds) =>
            {
                Number(row[0]).Should().Be(mySql ? timeoutSeconds * 1000 : timeoutSeconds);
                Number(row[1]).Should().Be(2);
                Number(row[2]).Should().Be(2);
            },
            WriteCheck = "table-privilege-not-allowlisted",
            GrantWriteAsync = () => ExecuteAsync($"GRANT INSERT ON `{admin.Database}`.session_items TO `{reader}`@`%`;"),
            RevokeWriteAsync = () => ExecuteAsync($"REVOKE INSERT ON `{admin.Database}`.session_items FROM `{reader}`@`%`;"),
            EndConnectionAsync = connectionId => ExecuteAsync($"KILL {connectionId};"),
            _cleanup = () => ExecuteAsync($"DROP USER IF EXISTS `{reader}`@`%`;")
        };
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(_cleanup());
    }

    private static decimal Number(JsonElement value)
    {
        return decimal.Parse(value.GetRawText().Trim('"'), CultureInfo.InvariantCulture);
    }

    private static async Task<DatabaseReadSessionDatabase> CreateSqlServerAsync()
    {
        var adminConnectionString = await RelationalTestServer.For(RelationalTestProvider.SqlServer)
            .CreateDatabaseAsync("session");
        var admin = new SqlConnectionStringBuilder(adminConnectionString);
        var user = admin.InitialCatalog + "_reader";
        var passwordKeyword = "PASS" + "WORD";

        async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await ExecuteAsync($"CREATE LOGIN [{user}] WITH {passwordKeyword} = '{Password}';");
        await ExecuteAsync("CREATE SCHEMA reporting;");
        await ExecuteAsync(
            $"""
            CREATE TABLE dbo.session_owners (id int NOT NULL PRIMARY KEY, name nvarchar(100) NOT NULL);
            CREATE TABLE dbo.session_items (
                id int NOT NULL PRIMARY KEY,
                owner_id int NOT NULL,
                name nvarchar(100) NOT NULL,
                CONSTRAINT fk_session_items_session_owners FOREIGN KEY (owner_id) REFERENCES dbo.session_owners (id)
            );
            CREATE INDEX ix_session_items_name ON dbo.session_items (name);
            CREATE TABLE dbo.session_restricted (id int NOT NULL PRIMARY KEY);
            CREATE TABLE dbo.session_wide ({WideColumns});
            CREATE TABLE reporting.session_totals (owner_id int NOT NULL, total int NOT NULL);
            INSERT INTO dbo.session_owners (id, name) VALUES (1, N'Owner');
            INSERT INTO dbo.session_items (id, owner_id, name) VALUES (1, 1, N'One'), (2, 1, N'Two'), (3, 1, N'Three');
            CREATE USER [{user}] FOR LOGIN [{user}];
            GRANT SELECT ON dbo.session_owners TO [{user}];
            GRANT SELECT ON dbo.session_items TO [{user}];
            GRANT SELECT ON dbo.session_wide TO [{user}];
            GRANT SELECT ON reporting.session_totals TO [{user}];
            """);

        return new DatabaseReadSessionDatabase
        {
            ProviderName = "sql-server",
            ReaderConnectionString = new SqlConnectionStringBuilder(adminConnectionString)
            {
                UserID = user,
                Password = Password,
                IntegratedSecurity = false
            }.ConnectionString,
            Schema = "dbo",
            OtherSchema = "reporting",
            ConnectionIdSql = "SELECT @@SPID",
            SlowSql =
                "SELECT SUM(CONVERT(bigint, a.column_id + b.column_id + c.column_id + d.column_id)) " +
                "FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b " +
                "CROSS JOIN sys.all_columns AS c CROSS JOIN sys.all_columns AS d",
            StateSql =
                "SELECT @@LOCK_TIMEOUT, candidate.deadlock_priority, @@TRANCOUNT " +
                "FROM sys.dm_exec_sessions AS candidate WHERE candidate.session_id = @@SPID",
            AssertRequestState = (row, _) =>
            {
                Number(row[0]).Should().Be(2_000);
                Number(row[1]).Should().Be(-5, "a diagnostic is the deadlock victim, not another session's change");
                Number(row[2]).Should().Be(1);
            },
            WriteCheck = "table-view-insert",
            GrantWriteAsync = () => ExecuteAsync($"GRANT INSERT ON dbo.session_items TO [{user}];"),
            RevokeWriteAsync = () => ExecuteAsync($"REVOKE INSERT ON dbo.session_items FROM [{user}];"),
            EndConnectionAsync = connectionId => ExecuteAsync($"KILL {connectionId};")
        };
    }

    private static async Task<DatabaseReadSessionDatabase> CreatePostgreSqlAsync()
    {
        var adminConnectionString = await RelationalTestServer.For(RelationalTestProvider.PostgreSql)
            .CreateDatabaseAsync("session");
        var admin = new NpgsqlConnectionStringBuilder(adminConnectionString);
        var user = admin.Database + "_reader";

        async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(
            $"""
            CREATE SCHEMA reporting;
            CREATE TABLE public.session_owners (id int NOT NULL PRIMARY KEY, name varchar(100) NOT NULL);
            CREATE TABLE public.session_items (
                id int NOT NULL PRIMARY KEY,
                owner_id int NOT NULL,
                name varchar(100) NOT NULL,
                CONSTRAINT fk_session_items_session_owners FOREIGN KEY (owner_id) REFERENCES public.session_owners (id)
            );
            CREATE INDEX ix_session_items_name ON public.session_items (name);
            CREATE TABLE public.session_restricted (id int NOT NULL PRIMARY KEY);
            CREATE TABLE public.session_wide ({WideColumns});
            CREATE TABLE reporting.session_totals (owner_id int NOT NULL, total int NOT NULL);
            INSERT INTO public.session_owners (id, name) VALUES (1, 'Owner');
            INSERT INTO public.session_items (id, owner_id, name) VALUES (1, 1, 'One'), (2, 1, 'Two'), (3, 1, 'Three');
            CREATE ROLE {user} LOGIN PASSWORD '{Password}';
            GRANT CONNECT ON DATABASE "{admin.Database}" TO {user};
            GRANT USAGE ON SCHEMA public, reporting TO {user};
            GRANT SELECT ON public.session_owners, public.session_items, public.session_wide, reporting.session_totals TO {user};
            """);

        return new DatabaseReadSessionDatabase
        {
            ProviderName = "postgresql",
            ReaderConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Username = user,
                Password = Password
            }.ConnectionString,
            Schema = "public",
            OtherSchema = "reporting",
            ConnectionIdSql = "SELECT pg_backend_pid()",
            SlowSql =
                "SELECT count(*) FROM pg_catalog.pg_attribute AS a " +
                "CROSS JOIN pg_catalog.pg_attribute AS b CROSS JOIN pg_catalog.pg_attribute AS c",
            StateSql =
                "SELECT current_setting('statement_timeout'), current_setting('lock_timeout'), " +
                "current_setting('transaction_read_only')",
            AssertRequestState = (row, timeoutSeconds) =>
            {
                row[0].GetString().Should().Be($"{timeoutSeconds}s");
                row[1].GetString().Should().Be("2s");
                row[2].GetString().Should().Be("on");
            },
            WriteCheck = "relation-write",
            GrantWriteAsync = () => ExecuteAsync($"GRANT INSERT ON public.session_items TO {user};"),
            RevokeWriteAsync = () => ExecuteAsync($"REVOKE INSERT ON public.session_items FROM {user};"),
            EndConnectionAsync = connectionId => ExecuteAsync($"SELECT pg_terminate_backend({connectionId});")
        };
    }
}
