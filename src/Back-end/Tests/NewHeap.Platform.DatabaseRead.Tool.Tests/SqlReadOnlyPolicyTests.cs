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
}
