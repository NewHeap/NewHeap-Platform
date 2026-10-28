using AwesomeAssertions;
using MySqlConnector;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// Grants that reach every account of the server run alone: a mandatory role on MySQL and a grant
/// to <c>PUBLIC</c> on MariaDB would reject the principals that other tests verify at the same
/// moment.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseReadMySqlServerWideGrantCollection
{
    public const string Name = "Database read MySQL server-wide grants";
}

/// <summary>
/// A role in MySQL's <c>mandatory_roles</c> is granted to every account and a privilege granted to
/// <c>PUBLIC</c> on MariaDB applies to every account, so the verification covers both: each
/// rejects a principal that passed before, until it is taken back.
/// </summary>
[Collection(DatabaseReadMySqlServerWideGrantCollection.Name)]
public sealed class DatabaseReadMySqlServerWideGrantTests
{
    private const string ReaderPassword = "Test-only-reader-Password-42";

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task GrantsToEveryAccountAreVerified(DatabaseReadMySqlFlavor flavor)
    {
        var server = DatabaseReadMySqlTestServer.For(flavor);
        var admin = new MySqlConnectionStringBuilder(await server.CreateDatabaseAsync("wide"));
        var database = admin.Database;
        var reader = $"`{database}_reader`@`%`";
        var mandatory = $"`{database}_mandatory`";
        await DatabaseReadMySqlTestServer.ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE TABLE items (id int NOT NULL PRIMARY KEY);
            CREATE USER {reader} IDENTIFIED BY '{ReaderPassword}';
            GRANT SELECT ON `{database}`.items TO {reader};
            CREATE ROLE {mandatory};
            GRANT DELETE ON `{database}`.items TO {mandatory};
            """);
        var readerConnectionString = DatabaseReadMySqlTestServer.AccountConnectionString(
            admin.ConnectionString,
            database + "_reader",
            ReaderPassword);
        var grant = flavor == DatabaseReadMySqlFlavor.MySql
            ? $"SET GLOBAL mandatory_roles = '{database}_mandatory@%'"
            : $"GRANT DELETE ON `{database}`.items TO PUBLIC";
        var revoke = flavor == DatabaseReadMySqlFlavor.MySql
            ? "SET GLOBAL mandatory_roles = ''"
            : $"REVOKE DELETE ON `{database}`.items FROM PUBLIC";
        var provider = new MySqlDatabaseReadProvider(server.Kind);

        try
        {
            (await DatabaseReadMySqlPrincipalVerificationTests.VerifyAsync(provider, readerConnectionString))
                .Should().Be(new DatabaseReadPrincipalVerification(true));

            await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, grant);
            try
            {
                (await DatabaseReadMySqlPrincipalVerificationTests.VerifyAsync(provider, readerConnectionString))
                    .Should().Be(new DatabaseReadPrincipalVerification(false, "table-privilege-not-allowlisted"));
            }
            finally
            {
                await DatabaseReadMySqlTestServer.ExecuteAsync(admin.ConnectionString, revoke);
            }

            (await DatabaseReadMySqlPrincipalVerificationTests.VerifyAsync(provider, readerConnectionString))
                .Should().Be(new DatabaseReadPrincipalVerification(true));
        }
        finally
        {
            await DatabaseReadMySqlTestServer.ExecuteAsync(
                admin.ConnectionString,
                $"DROP USER IF EXISTS {reader}; DROP ROLE IF EXISTS {mandatory};");
        }
    }
}
