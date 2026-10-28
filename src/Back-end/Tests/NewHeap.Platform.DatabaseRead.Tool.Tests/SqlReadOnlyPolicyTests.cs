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
    /// These functions run SQL text that the policy reads as a string literal, or sleep like
    /// <c>pg_sleep</c>.
    /// </summary>
    [Theory]
    [InlineData("SELECT query_to_xml('SELECT pg_sleep(1)', true, true, '')")]
    [InlineData("SELECT query_to_xmlschema('SELECT pg_sleep(1)', true, true, '')")]
    [InlineData("SELECT query_to_xml_and_xmlschema('SELECT pg_sleep(1)', true, true, '')")]
    [InlineData("SELECT cursor_to_xml('diagnostic_cursor', 1, true, true, '')")]
    [InlineData("SELECT cursor_to_xmlschema('diagnostic_cursor', true, true, '')")]
    [InlineData("SELECT * FROM ts_stat('SELECT to_tsvector(pg_sleep(1)::text)')")]
    [InlineData("SELECT ts_rewrite('a'::tsquery, 'SELECT ''a''::tsquery, ''b''::tsquery FROM pg_sleep(1)')")]
    [InlineData("SELECT pg_catalog.\"query_to_xml\"('SELECT pg_sleep(1)', true, true, '')")]
    [InlineData("SELECT pg_sleep_for('1 second')")]
    [InlineData("SELECT pg_sleep_until(now())")]
    public void PostgreSqlFunctionsThatRunSqlTextOrSleepAreRejected(string sql)
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

    /// <summary>
    /// In an escape string a backslash escapes the quote that would otherwise end it, which the
    /// policy does not follow, so <c>E'\''</c> could hide the SQL behind it from the policy.
    /// </summary>
    [Theory]
    [InlineData("SELECT E'\\'' AS a, pg_sleep(1) AS b, E'\\''")]
    [InlineData("SELECT e'value' AS v")]
    public void PostgreSqlEscapeStringsAreRejected(string sql)
    {
        var action = () => SqlReadOnlyPolicy.Validate(sql, DatabaseProviderKind.PostgreSql);

        action.Should()
            .Throw<DatabaseReadExpectedException>()
            .Which.Code.Should().Be("escape-string-not-allowed");
    }

    [Theory]
    [InlineData("SELECT 'it''s' AS e, 'C:\\' AS path", "postgresql")]
    [InlineData("SELECT name AS e FROM projects WHERE name LIKE 'a\\_%' LIMIT 1", "postgresql")]
    [InlineData("SELECT TOP (1) N'it''s' AS E, Name AS e FROM Projects", "sql-server")]
    public void StandardStringsAndIdentifiersNamedEAreAllowed(string sql, string providerName)
    {
        var provider = providerName == "sql-server"
            ? DatabaseProviderKind.SqlServer
            : DatabaseProviderKind.PostgreSql;
        var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

        action.Should().NotThrow();
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

    /// <summary>
    /// MySQL and MariaDB also read <c>#</c> as a comment, take row locks with <c>FOR SHARE</c> and
    /// <c>LOCK IN SHARE MODE</c>, and have functions that sleep, take user locks, read server files
    /// or move a sequence; a backtick- or double-quoted name of such a function is refused too.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1 # hidden second statement", "comments-not-allowed")]
    [InlineData("SELECT 1 -- hidden", "comments-not-allowed")]
    [InlineData("SELECT /*!50000 SLEEP(1) */ 1", "comments-not-allowed")]
    [InlineData("SELECT /*+ MAX_EXECUTION_TIME(0) */ id FROM items", "comments-not-allowed")]
    [InlineData("SELECT SLEEP(1)", "function-not-allowed")]
    [InlineData("SELECT `sleep`(1)", "function-not-allowed")]
    [InlineData("SELECT `app`.`Sleep`(1)", "function-not-allowed")]
    [InlineData("SELECT \"SLEEP\"(1)", "function-not-allowed")]
    [InlineData("SELECT BENCHMARK(1000000, MD5('probe'))", "function-not-allowed")]
    [InlineData("SELECT GET_LOCK('probe', 10)", "function-not-allowed")]
    [InlineData("SELECT RELEASE_ALL_LOCKS()", "function-not-allowed")]
    [InlineData("SELECT LOAD_FILE('/etc/hostname')", "function-not-allowed")]
    [InlineData("SELECT NEXTVAL(item_numbers)", "function-not-allowed")]
    [InlineData("SELECT NEXT VALUE FOR item_numbers", "function-not-allowed")]
    [InlineData("SELECT id FROM items FOR SHARE", "locking-read-not-allowed")]
    [InlineData("SELECT id FROM items LOCK IN SHARE MODE", "query-not-read-only")]
    [InlineData("SELECT id FROM items FOR UPDATE", "query-not-read-only")]
    [InlineData("SELECT id INTO @value FROM items", "query-not-read-only")]
    [InlineData("SELECT id FROM items INTO OUTFILE '/tmp/items'", "query-not-read-only")]
    [InlineData("SET STATEMENT max_statement_time = 0 FOR SELECT id FROM items", "query-not-read-only")]
    [InlineData("SELECT 1; SELECT 2", "multiple-statements-not-allowed")]
    public void UnsafeMySqlFamilySqlIsRejected(string sql, string expectedCode)
    {
        foreach (var provider in new[] { DatabaseProviderKind.MySql, DatabaseProviderKind.MariaDb })
        {
            var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

            action.Should()
                .Throw<DatabaseReadExpectedException>()
                .Which.Code.Should().Be(expectedCode, "on {0}", provider);
        }
    }

    /// <summary>
    /// A backslash escapes a quote in a MySQL string unless the session's <c>sql_mode</c> has
    /// <c>NO_BACKSLASH_ESCAPES</c>, so <c>'x\''</c> ends elsewhere in one mode than in the other and
    /// could hide the SQL behind it from the policy. A quoted value with a backslash is refused.
    /// </summary>
    [Theory]
    [InlineData("SELECT 'x\\'' , SLEEP(1) , ''")]
    [InlineData("SELECT 'C:\\temp' AS path")]
    [InlineData("SELECT \"a\\\"\" AS a")]
    [InlineData("SELECT name FROM items WHERE name LIKE 'a\\_%' LIMIT 1")]
    public void MySqlFamilyBackslashesInQuotedValuesAreRejected(string sql)
    {
        foreach (var provider in new[] { DatabaseProviderKind.MySql, DatabaseProviderKind.MariaDb })
        {
            var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

            action.Should()
                .Throw<DatabaseReadExpectedException>()
                .Which.Code.Should().Be("backslash-escape-not-allowed");
        }
    }

    [Theory]
    [InlineData("SELECT `id` AS `Update`, `name` AS `Share`, `sleep_count` FROM `app`.`items` LIMIT 1")]
    [InlineData("SELECT 'it''s' AS a, \"say \"\"hi\"\"\" AS b, '#' AS c, 'a -- b' AS d")]
    [InlineData("SELECT name FROM items WHERE name LIKE 'a!_%' ESCAPE '!' LIMIT 1")]
    [InlineData("WITH recent AS (SELECT id FROM items ORDER BY id DESC LIMIT 5) SELECT id FROM recent")]
    [InlineData("SELECT id, IS_FREE_LOCK('probe') AS free, PREVIOUS VALUE FOR item_numbers FROM items LIMIT 1")]
    [InlineData("SELECT `a``b` FROM items LIMIT 1;")]
    public void MySqlFamilyReadsAreAllowed(string sql)
    {
        foreach (var provider in new[] { DatabaseProviderKind.MySql, DatabaseProviderKind.MariaDb })
        {
            var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

            action.Should().NotThrow("on {0}", provider);
        }
    }

    /// <summary>
    /// The MySQL rules stay out of the other providers: SQL Server and PostgreSQL keep their own
    /// reading of <c>#</c> and of backslashes.
    /// </summary>
    [Theory]
    [InlineData("SELECT TOP (1) Id AS [#] FROM dbo.Projects WHERE Name <> '#'", "sql-server")]
    [InlineData("SELECT 'C:\\temp' AS path", "postgresql")]
    [InlineData("SELECT 'C:\\temp' AS path", "sql-server")]
    public void MySqlRulesDoNotApplyToTheOtherProviders(string sql, string providerName)
    {
        var provider = providerName == "sql-server"
            ? DatabaseProviderKind.SqlServer
            : DatabaseProviderKind.PostgreSql;
        var action = () => SqlReadOnlyPolicy.Validate(sql, provider);

        action.Should().NotThrow();
    }
}
