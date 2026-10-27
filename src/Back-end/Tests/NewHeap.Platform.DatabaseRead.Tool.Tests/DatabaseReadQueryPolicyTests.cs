using AwesomeAssertions;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// On real PostgreSQL, no text that the SQL policy takes for a string literal runs as SQL: neither
/// a literal whose end a backslash hides nor the SQL text that a function such as
/// <c>query_to_xml</c> runs.
/// </summary>
/// <remarks>
/// The roles belong to the server that the test process shares, so their names start with the name
/// of the test's own database, and each test drops its role when it ends.
/// </remarks>
public sealed class DatabaseReadQueryPolicyTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

    /// <summary>
    /// The principal's role turns standard-conforming strings off, where PostgreSQL would read a
    /// backslash in an ordinary string literal as an escape.
    /// </summary>
    [Fact]
    public async Task PostgreSqlStringLiteralsCannotHideAForbiddenFunction()
    {
        var admin = new NpgsqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.PostgreSql).CreateDatabaseAsync("strings"));
        var reader = admin.Database + "_reader";
        await ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE ROLE {reader} LOGIN PASSWORD '{ReaderPassword}';
            GRANT CONNECT ON DATABASE "{admin.Database}" TO {reader};
            ALTER ROLE {reader} SET standard_conforming_strings = off;
            """);
        var readerConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Username = reader,
            Password = ReaderPassword,
            Pooling = false
        }.ConnectionString;

        try
        {
            using var workspace = new DatabaseReadTestWorkspace("postgresql", readerConnectionString);

            using (var escaped = await QueryAsync(
                       workspace,
                       "SELECT E'\\'' AS a, pg_sleep(0) AS b, E'\\''"))
            {
                escaped.RootElement.GetProperty("error").GetProperty("code").GetString()
                    .Should().Be("escape-string-not-allowed");
                escaped.RootElement.TryGetProperty("result", out _).Should().BeFalse();
            }

            using (var plain = await QueryAsync(
                       workspace,
                       "SELECT '\\'' AS a, pg_sleep(0) AS b, '\\''"))
            {
                plain.RootElement.TryGetProperty("result", out _).Should().BeFalse(
                    "the backslash must not end the literal early, so the function inside it never runs");
                plain.RootElement.GetProperty("error").GetProperty("classification").GetString()
                    .Should().Be("syntax-error");
            }

            using var path = await QueryAsync(workspace, "SELECT 'C:\\' AS path");
            path.RootElement.GetProperty("result").GetProperty("rows")[0][0].GetString().Should().Be("C:\\");
        }
        finally
        {
            await ExecuteAsync(
                admin.ConnectionString,
                $"""
                DROP OWNED BY {reader};
                DROP ROLE {reader};
                """);
        }
    }

    /// <summary>
    /// <c>query_to_xml</c> runs the SQL text it receives, which the policy reads as a string, so a
    /// forbidden function inside it would run unseen. A session advisory lock taken that way stays
    /// held by the pooled session after the tool rolls the transaction back, so the reader keeps the
    /// default pooling here and the test checks that no such lock exists afterwards.
    /// </summary>
    [Fact]
    public async Task PostgreSqlFunctionsThatRunSqlTextAreRefused()
    {
        var admin = new NpgsqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.PostgreSql).CreateDatabaseAsync("sqltext"));
        var reader = admin.Database + "_reader";
        await ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE ROLE {reader} LOGIN PASSWORD '{ReaderPassword}';
            GRANT CONNECT ON DATABASE "{admin.Database}" TO {reader};
            """);
        var readerConnectionString = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
        {
            Username = reader,
            Password = ReaderPassword
        }.ConnectionString;

        try
        {
            using var workspace = new DatabaseReadTestWorkspace("postgresql", readerConnectionString);
            var statements = new[]
            {
                "SELECT query_to_xml('SELECT pg_advisory_lock(hashtextextended(''sql-text-probe'', 0))', true, true, '')",
                "SELECT pg_catalog.query_to_xml_and_xmlschema('SELECT pg_sleep(0)', true, true, '')",
                "SELECT \"query_to_xmlschema\"('SELECT pg_sleep(0)', true, true, '')",
                "SELECT * FROM ts_stat('SELECT to_tsvector(''simple'', pg_sleep(0)::text)')",
                "SELECT ts_rewrite('a'::tsquery, 'SELECT ''a''::tsquery, ''b''::tsquery FROM pg_sleep(0)')",
                "SELECT pg_sleep_for('0 seconds')",
                "SELECT pg_sleep_until(now())"
            };

            foreach (var sql in statements)
            {
                using var response = await QueryAsync(workspace, sql);
                response.RootElement.TryGetProperty("result", out _).Should().BeFalse(sql);
                response.RootElement.GetProperty("error").GetProperty("code").GetString()
                    .Should().Be("function-not-allowed", sql);
            }

            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var locks = connection.CreateCommand();
            locks.CommandText =
                """
                SELECT count(*)
                FROM pg_catalog.pg_locks AS held
                INNER JOIN pg_catalog.pg_stat_activity AS session ON session.pid = held.pid
                WHERE held.locktype = 'advisory' AND session.usename = @reader
                """;
            locks.Parameters.AddWithValue("reader", reader);
            ((long)(await locks.ExecuteScalarAsync())!).Should().Be(0);
        }
        finally
        {
            await ExecuteAsync(
                admin.ConnectionString,
                $"""
                SELECT pg_catalog.pg_terminate_backend(pid)
                FROM pg_catalog.pg_stat_activity
                WHERE usename = '{reader}';
                DROP OWNED BY {reader};
                DROP ROLE {reader};
                """);
        }
    }

    private static async Task<JsonDocument> QueryAsync(DatabaseReadTestWorkspace workspace, string sql)
    {
        using var input = DatabaseReadTestWorkspace.Request(sql);
        using var output = new MemoryStream();

        await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        return DatabaseReadTestWorkspace.ParseOutput(output);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
