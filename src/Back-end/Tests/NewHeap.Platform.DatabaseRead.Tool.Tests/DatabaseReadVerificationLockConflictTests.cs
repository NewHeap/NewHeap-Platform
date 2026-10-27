using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// SQL Server principal verification reads the permission catalog, which a concurrent change of
/// logins or grants locks. A transaction that grants a permission on a login and stays open holds
/// those locks for as long as the test wants: the verification must wait it out with retries,
/// yield deadlocks to the other session, and fail rather than pass when the conflict outlasts the
/// request's timeout.
/// </summary>
/// <remarks>
/// The logins belong to the server that the test process shares, so their names start with the
/// name of the test's own database, and the test drops them when it ends.
/// </remarks>
public sealed class DatabaseReadVerificationLockConflictTests
{
    private const string Password = "Test-only-reader-Password-42";

    /// <summary>
    /// A short lock timeout, so a blocked attempt ends quickly, and a request timeout that leaves
    /// time for the attempts after the conflict ends, also while other tests change logins.
    /// </summary>
    private static readonly DatabaseReadLimits Limits = new(10, 15, 500, 65_536, 4_096, 8_192);

    /// <summary>
    /// The same lock timeout with a request timeout that runs out within the test.
    /// </summary>
    private static readonly DatabaseReadLimits ShortLimits = new(10, 3, 500, 65_536, 4_096, 8_192);

    [Fact]
    public async Task SqlServerVerificationRetriesAfterALockConflictAndNeverPassesOnOne()
    {
        var admin = new SqlConnectionStringBuilder(
            await RelationalTestServer.For(RelationalTestProvider.SqlServer).CreateDatabaseAsync("lockconflict"));
        var master = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = "master" };
        var reader = admin.InitialCatalog + "_reader";
        var grantee = admin.InitialCatalog + "_grantee";
        var target = admin.InitialCatalog + "_target";
        var passwordKeyword = "PASS" + "WORD";
        await ExecuteAsync(
            master.ConnectionString,
            $"""
            CREATE LOGIN [{reader}] WITH {passwordKeyword} = '{Password}';
            CREATE LOGIN [{grantee}] WITH {passwordKeyword} = '{Password}';
            CREATE LOGIN [{target}] WITH {passwordKeyword} = '{Password}';
            """);
        await ExecuteAsync(
            admin.ConnectionString,
            $"CREATE USER [{reader}] FOR LOGIN [{reader}]; CREATE TABLE dbo.Items (Id int); GRANT SELECT ON dbo.Items TO [{reader}];");
        var readerConnectionString = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = reader,
            Password = Password,
            IntegratedSecurity = false,
            Pooling = false
        }.ConnectionString;
        var provider = new SqlServerDatabaseReadProvider();

        try
        {
            await using (var blocker = await BeginConflictingGrantAsync(master.ConnectionString, grantee, target))
            await using (var connection = provider.CreateConnection(
                             readerConnectionString,
                             Guid.NewGuid().ToString("N"),
                             Limits))
            {
                await connection.OpenAsync();
                var sessionId = await GetSessionIdAsync(connection);
                var verification = provider.VerifyReadOnlyPrincipalAsync(connection, Limits, CancellationToken.None);

                try
                {
                    var firstAttempt = await WaitForBlockedVerificationAsync(master.ConnectionString, sessionId, null);
                    firstAttempt.DeadlockPriority.Should().Be(
                        -5,
                        "the verification yields deadlocks to the other session");
                    await WaitForBlockedVerificationAsync(master.ConnectionString, sessionId, firstAttempt.StartTime);
                }
                finally
                {
                    // Ends the conflict and lets the verification finish before its connection closes,
                    // also when an assertion above failed; its outcome is asserted below.
                    await blocker.Transaction.RollbackAsync();
                    await Task.WhenAny(verification);
                }

                (await verification).Should().Be(new DatabaseReadPrincipalVerification(true));
            }

            await using (var blocker = await BeginConflictingGrantAsync(master.ConnectionString, grantee, target))
            {
                await using var connection = provider.CreateConnection(
                    readerConnectionString,
                    Guid.NewGuid().ToString("N"),
                    ShortLimits);
                await connection.OpenAsync();

                var verify = () => provider.VerifyReadOnlyPrincipalAsync(connection, ShortLimits, CancellationToken.None);

                var failure = await verify.Should().ThrowAsync<SqlException>();
                SqlServerDatabaseReadProvider.IsLockConflict(failure.Which).Should().BeTrue(failure.Which.Message);
                await blocker.Transaction.RollbackAsync();
            }
        }
        finally
        {
            await ExecuteAsync(admin.ConnectionString, $"DROP USER [{reader}];");
            await DropLoginsAsync(master.ConnectionString, reader, grantee, target);
        }
    }

    /// <summary>
    /// Grants a permission on one login to another in a transaction that stays open, so the grant's
    /// catalog row and the login's permission metadata stay locked until the test ends it.
    /// </summary>
    private static async Task<OpenGrant> BeginConflictingGrantAsync(
        string masterConnectionString,
        string grantee,
        string target)
    {
        var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"GRANT ALTER ON LOGIN::[{target}] TO [{grantee}];";
        await command.ExecuteNonQueryAsync();

        return new OpenGrant(connection, transaction);
    }

    /// <summary>
    /// Waits until the verification of the request waits for a lock, in an attempt that started
    /// after <paramref name="after"/> when it is given. The bound only matters when the test fails.
    /// </summary>
    private static async Task<BlockedAttempt> WaitForBlockedVerificationAsync(
        string masterConnectionString,
        short sessionId,
        DateTime? after)
    {
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT start_time, deadlock_priority
                    FROM sys.dm_exec_requests
                    WHERE session_id = @sessionId AND wait_type LIKE 'LCK[_]%'
                    """;
                command.CommandTimeout = 10;
                command.Parameters.AddWithValue("@sessionId", sessionId);
                await using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var attempt = new BlockedAttempt(reader.GetDateTime(0), reader.GetInt32(1));
                    if (after is null || attempt.StartTime > after)
                    {
                        return attempt;
                    }
                }
            }

            DateTime.UtcNow.Should().BeBefore(deadline, "the verification should wait for the open grant");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    private static async Task DropLoginsAsync(string masterConnectionString, params string[] logins)
    {
        foreach (var login in logins)
        {
            await ExecuteAsync(masterConnectionString, $"DROP LOGIN [{login}];");
        }
    }

    private static async Task<short> GetSessionIdAsync(System.Data.Common.DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@@SPID AS smallint);";

        return (short)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record BlockedAttempt(DateTime StartTime, int DeadlockPriority);

    private sealed class OpenGrant(SqlConnection connection, SqlTransaction transaction) : IAsyncDisposable
    {
        public SqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
