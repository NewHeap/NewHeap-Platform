using AwesomeAssertions;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The lines that <c>SHOW GRANTS</c> returns on MySQL 8.4 and MariaDB 11.4, read without a server:
/// only role grants, <c>USAGE</c> and SELECT outside the system schemas pass, and a line the policy
/// cannot read rejects the principal.
/// </summary>
public sealed class MySqlGrantPolicyTests
{
    [Theory]
    [InlineData("GRANT USAGE ON *.* TO `reader`@`%`")]
    [InlineData("GRANT USAGE ON *.* TO `reader`@`%` IDENTIFIED BY PASSWORD '*5239E527C7667DB0F5C28072D86C77D2D2BE0ADA'")]
    [InlineData("GRANT SELECT ON `app`.`items` TO `reader`@`%`")]
    [InlineData("GRANT SELECT (`id`, `name`) ON `app`.`items` TO `reader`@`%`")]
    [InlineData("GRANT SELECT, SELECT (`id`) ON `app`.`other` TO `reader`@`%`")]
    [InlineData("GRANT SELECT ON `app`.* TO `reader`@`%`")]
    [InlineData("GRANT SELECT ON `app\\_x`.* TO `reader`@`%`")]
    [InlineData("GRANT `outer_r`@`%`,`writer`@`%` TO `reader`@`%`")]
    [InlineData("GRANT `writer` TO `reader`@`%`")]
    [InlineData("GRANT SELECT ON `app`.`items` TO PUBLIC")]
    [InlineData("GRANT SELECT ON `a``b`.`it``ems` TO `reader`@`%`")]
    [InlineData("REVOKE INSERT ON `mysql`.* FROM `reader`@`%`")]
    public void ReadOnlyLinesPass(string line)
    {
        MySqlGrantPolicy.Classify(line, DatabaseProviderKind.MySql).Should().BeNull();
        MySqlGrantPolicy.Classify(line, DatabaseProviderKind.MariaDb).Should().BeNull();
    }

    [Theory]
    [InlineData("GRANT SELECT ON *.* TO `reader`@`%`", "global-privilege")]
    [InlineData("GRANT SELECT, SHOW DATABASES ON *.* TO `g1`@`%`", "global-privilege")]
    [InlineData("GRANT PROCESS ON *.* TO `writer`@`%`", "global-privilege")]
    [InlineData("GRANT BACKUP_ADMIN,SHOW_ROUTINE ON *.* TO `g1`@`%`", "dynamic-privilege")]
    [InlineData("GRANT PROXY ON `reader`@`%` TO `g1`@`%`", "proxy-privilege")]
    [InlineData("GRANT PROXY ON ``@`` TO `root`@`localhost` WITH GRANT OPTION", "proxy-privilege")]
    [InlineData("GRANT SELECT ON `app`.`items` TO `g1`@`%` WITH GRANT OPTION", "grant-option")]
    [InlineData("GRANT USAGE ON *.* TO `g1`@`%` WITH GRANT OPTION", "grant-option")]
    [InlineData("GRANT `writer`@`%` TO `g1`@`%` WITH ADMIN OPTION", "role-admin-option")]
    [InlineData("GRANT SELECT ON `mysql`.`user` TO `g1`@`%`", "system-schema-select")]
    [InlineData("GRANT SELECT (`User`) ON `mysql`.`user` TO `g1`@`%`", "system-schema-select")]
    [InlineData("GRANT SELECT ON `performance_schema`.* TO `g1`@`%`", "system-schema-select")]
    [InlineData("GRANT SELECT ON `my_ql`.* TO `g1`@`%`", "system-schema-select")]
    [InlineData("GRANT SELECT ON `%`.* TO `g1`@`%`", "system-schema-select")]
    [InlineData("GRANT SELECT ON `sy%`.* TO `g1`@`%`", "system-schema-select")]
    [InlineData("GRANT CREATE TEMPORARY TABLES, LOCK TABLES ON `app`.* TO `g1`@`%`", "schema-privilege-not-allowlisted")]
    [InlineData("GRANT SHOW VIEW ON `app`.* TO `g1`@`%`", "schema-privilege-not-allowlisted")]
    [InlineData("GRANT ALL PRIVILEGES ON `app`.* TO `g1`@`%`", "schema-privilege-not-allowlisted")]
    [InlineData("GRANT UPDATE, CREATE ON `app`.* TO `reader`@`%`", "schema-privilege-not-allowlisted")]
    [InlineData("GRANT SELECT, INSERT ON `app`.`items` TO `reader`@`%`", "table-privilege-not-allowlisted")]
    [InlineData("GRANT SHOW VIEW ON `app`.`names` TO `reader`@`%`", "table-privilege-not-allowlisted")]
    [InlineData("GRANT SELECT (`id`), UPDATE (`name`) ON `app`.`limited` TO `g1`@`%`", "column-privilege-not-allowlisted")]
    [InlineData("GRANT EXECUTE ON FUNCTION `app`.`f_write` TO `g1`@`%`", "routine-privilege")]
    [InlineData("GRANT EXECUTE, ALTER ROUTINE ON PROCEDURE `app`.`p` TO `g1`@`%`", "routine-privilege")]
    [InlineData("GRANT EXECUTE ON PACKAGE BODY `app`.`p` TO `g1`@`%`", "routine-privilege")]
    [InlineData("GRANT SELECT ON TABLE `app`.`items` TO `g1`@`%`", "grant-not-recognized")]
    [InlineData("GRANT SELECT ON `app`.`items` TO `g1`@`%", "grant-not-recognized")]
    [InlineData("GRANT SELECT `app`.`items` `g1`@`%`", "grant-not-recognized")]
    [InlineData("SET DEFAULT ROLE ALL TO `g1`@`%`", "grant-not-recognized")]
    [InlineData("", "grant-not-recognized")]
    public void ElevatedOrUnknownLinesNameTheirCheck(string line, string expectedCheck)
    {
        MySqlGrantPolicy.Classify(line, DatabaseProviderKind.MySql).Should().Be(expectedCheck);
    }

    /// <summary>
    /// MariaDB writes its server privileges with spaces and has no dynamic privileges, so every
    /// one of them is a global privilege.
    /// </summary>
    [Theory]
    [InlineData("GRANT SELECT, SHOW DATABASES, BINLOG MONITOR, SLAVE MONITOR ON *.* TO `g1`@`%` IDENTIFIED BY PASSWORD '*1BF48725'")]
    [InlineData("GRANT READ_ONLY ADMIN ON *.* TO `g1`@`%`")]
    public void MariaDbServerPrivilegesAreGlobalPrivileges(string line)
    {
        MySqlGrantPolicy.Classify(line, DatabaseProviderKind.MariaDb).Should().Be("global-privilege");
    }

    [Fact]
    public void TheFirstCheckInTheFixedOrderIsReported()
    {
        MySqlGrantPolicy.First(["definer-view", "table-privilege-not-allowlisted", "grant-option"])
            .Should().Be("grant-option");
        MySqlGrantPolicy.First([]).Should().BeNull();
        MySqlGrantPolicy.Classify(
                "GRANT SELECT, INSERT ON `mysql`.`user` TO `g1`@`%` WITH GRANT OPTION",
                DatabaseProviderKind.MySql)
            .Should().Be("grant-option");
    }

    [Theory]
    [InlineData("mysql", true)]
    [InlineData("MySQL", true)]
    [InlineData("my_ql", true)]
    [InlineData("m%", true)]
    [InlineData("my\\_ql", false)]
    [InlineData("mysql\\_archive", false)]
    [InlineData("app", false)]
    [InlineData("app%", false)]
    [InlineData("sys", true)]
    [InlineData("performance\\_schema", true)]
    public void SchemaPatternsMatchSystemSchemasAsTheServerWould(string pattern, bool expected)
    {
        MySqlGrantPolicy.MatchesSystemSchema(pattern).Should().Be(expected);
    }
}
