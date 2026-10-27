using AwesomeAssertions;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The SQL policy reads string literals as PostgreSQL does when it runs the query, so no text that
/// the policy took for a string literal runs as SQL. The principal's role turns standard-conforming
/// strings off, where PostgreSQL would read a backslash in a plain string literal as an escape.
/// </summary>
/// <remarks>
/// The role belongs to the server that the test process shares, so its name starts with the name of
/// the test's own database, and the test drops it when it ends.
/// </remarks>
public sealed class DatabaseReadQueryPolicyTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

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
