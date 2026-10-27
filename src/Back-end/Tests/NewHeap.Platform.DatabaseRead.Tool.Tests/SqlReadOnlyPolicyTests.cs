using AwesomeAssertions;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

public sealed class SqlReadOnlyPolicyTests
{
    [Fact]
    public void QuotedMutationWordsAndSemicolonsAreNotTreatedAsSqlTokens()
    {
        var action = () => SqlReadOnlyPolicy.Validate(
            "SELECT [Update], 'DELETE; UPDATE' AS Message FROM [Set];",
            DatabaseProviderKind.SqlServer);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("SELECT * FROM Projects WITH (XLOCK)", "sql-server", "locking-hint-not-allowed")]
    [InlineData("SELECT pg_sleep(1)", "postgresql", "function-not-allowed")]
    [InlineData("SELECT pg_read_file('/var/lib/postgresql/data/postgresql.conf')", "postgresql", "function-not-allowed")]
    [InlineData("WITH changed AS (UPDATE Projects SET Name = 'x' RETURNING *) SELECT * FROM changed", "postgresql", "query-not-read-only")]
    [InlineData("SELECT 1; SELECT 2", "sql-server", "multiple-statements-not-allowed")]
    [InlineData("SELECT 1 -- hidden second statement", "sql-server", "comments-not-allowed")]
    public void UnsafeSqlIsRejected(
        string sql,
        string providerName,
        string expectedCode)
    {
        var provider = providerName == "sql-server"
            ? DatabaseProviderKind.SqlServer
            : DatabaseProviderKind.PostgreSql;
        var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

        action.Should()
            .Throw<DatabaseReadExpectedException>()
            .Which.Code.Should().Be(expectedCode);
    }

    /// <summary>
    /// PostgreSQL resolves a double-quoted lowercase name to the same function as the unquoted name,
    /// so a quoted, schema-qualified or case-varied spelling must not pass the function list.
    /// </summary>
    [Theory]
    [InlineData("SELECT \"pg_sleep\"(1)")]
    [InlineData("SELECT pg_catalog.\"pg_sleep\"(1)")]
    [InlineData("SELECT \"pg_catalog\".\"pg_sleep\"(1)")]
    [InlineData("SELECT \"PG_SLEEP\"(1)")]
    [InlineData("SELECT \"Pg_Sleep\" (1)")]
    [InlineData("SELECT \"pg_advisory_lock\"(hashtextextended('probe', 0))")]
    [InlineData("SELECT \"pg_terminate_backend\"(12345)")]
    [InlineData("SELECT \"pg_read_file\"('/etc/hostname')")]
    [InlineData("SELECT \"pg_logical_emit_message\"(false, 'probe', 'probe')")]
    [InlineData("SELECT \"set_config\"('statement_timeout', '0', false)")]
    [InlineData("SELECT \"dblink\"('dbname=other', 'SELECT 1')")]
    public void QuotedPostgreSqlFunctionNamesAreRejected(string sql)
    {
        var action = () => SqlReadOnlyPolicy.Validate(sql, DatabaseProviderKind.PostgreSql);

        action.Should()
            .Throw<DatabaseReadExpectedException>()
            .Which.Code.Should().Be("function-not-allowed");
    }

    /// <summary>
    /// A Unicode-escaped identifier spells a name the policy cannot read, such as
    /// <c>U&amp;"pg\005fsleep"</c> for <c>pg_sleep</c>, so it is refused as a whole.
    /// </summary>
    [Theory]
    [InlineData("SELECT U&\"pg\\005fsleep\"(1)")]
    [InlineData("SELECT u&\"pg_sleep\"(1)")]
    public void UnicodeEscapedPostgreSqlIdentifiersAreRejected(string sql)
    {
        var action = () => SqlReadOnlyPolicy.Validate(sql, DatabaseProviderKind.PostgreSql);

        action.Should()
            .Throw<DatabaseReadExpectedException>()
            .Which.Code.Should().Be("unicode-escaped-identifier-not-allowed");
    }

    [Theory]
    [InlineData("SELECT \"Id\", \"Update\", \"share\", \"pg_sleep_count\" FROM \"public\".\"Projects\" LIMIT 1", "postgresql")]
    [InlineData("SELECT U&'caf\\00e9' AS \"Name\" FROM \"Projects\" LIMIT 1", "postgresql")]
    [InlineData("SELECT TOP (1) [pg_sleep], \"pg_sleep\" FROM [dbo].[Projects]", "sql-server")]
    public void QuotedIdentifiersThatNameNoForbiddenFunctionAreAllowed(string sql, string providerName)
    {
        var provider = providerName == "sql-server"
            ? DatabaseProviderKind.SqlServer
            : DatabaseProviderKind.PostgreSql;
        var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

        action.Should().NotThrow();
    }
}
