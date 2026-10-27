using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using System.Data.Common;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// SQL Server principal verification reads the permission catalog, which a concurrent change of
/// grants locks. A transaction that grants a permission on a role and stays open holds those locks
/// for as long as the test wants: the verification must wait it out with retries, yield deadlocks to
/// the other session, and fail rather than pass when the conflict outlasts the request's timeout.
/// </summary>
/// <remarks>
/// The open grant stays inside the test's own database: a grant on a login would lock permission
/// rows in <c>master</c>, which the verifications of every other test on the shared server read, and
/// stall them for as long as the grant stays open. The test opens every connection it needs before
/// it starts the grant, so none of them has to log in while the grant holds its locks. The reader's
/// login belongs to the shared server, so its name starts with the name of the test's database, and
/// the test drops it when it ends.
/// </remarks>
public sealed class DatabaseReadVerificationLockConflictTests
{
    private const string Password = "Test-only-reader-Password-42";

    /// <summary>
    /// A short lock timeout, so a blocked attempt ends quickly, and a request timeout that leaves
    /// time for the attempts after the conflict ends.
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
        const string grantee = "diagnostic_grantee";
        const string role = "diagnostic_role";
        var passwordKeyword = "PASS" + "WORD";
        await ExecuteAsync(master.ConnectionString, $"CREATE LOGIN [{reader}] WITH {passwordKeyword} = '{Password}';");
        await ExecuteAsync(
            admin.ConnectionString,
            $"""
            CREATE USER [{reader}] FOR LOGIN [{reader}];
            CREATE USER [{grantee}] WITHOUT LOGIN;
            CREATE ROLE [{role}];
            CREATE TABLE dbo.Items (Id int);
            GRANT SELECT ON dbo.Items TO [{reader}];
            """);
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
            await using (var monitor = await OpenAsync(admin.ConnectionString))
            await using (var connection = await OpenVerificationConnectionAsync(provider, readerConnectionString, Limits))
            await using (var blocker = await BeginConflictingGrantAsync(admin.ConnectionString, role, grantee))
            {
                (await CountLocksInMasterAsync(monitor, blocker.SessionId)).Should().Be(
                    0,
                    "the open grant must not lock anything that other tests' sessions read in master");
                var sessionId = await GetSessionIdAsync(connection);
                var verification = provider.VerifyReadOnlyPrincipalAsync(connection, Limits, CancellationToken.None);

                try
                {
                    var firstAttempt = await WaitForBlockedVerificationAsync(monitor, sessionId, null);
                    firstAttempt.DeadlockPriority.Should().Be(
                        -5,
                        "the verification yields deadlocks to the other session");
                    await WaitForBlockedVerificationAsync(monitor, sessionId, firstAttempt.StartTime);
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

            await using (var connection = await OpenVerificationConnectionAsync(provider, readerConnectionString, ShortLimits))
            await using (var blocker = await BeginConflictingGrantAsync(admin.ConnectionString, role, grantee))
            {
                var verify = () => provider.VerifyReadOnlyPrincipalAsync(connection, ShortLimits, CancellationToken.None);

                var failure = await verify.Should().ThrowAsync<SqlException>();
                SqlServerDatabaseReadProvider.IsLockConflict(failure.Which).Should().BeTrue(failure.Which.Message);
                await blocker.Transaction.RollbackAsync();
            }
        }
        finally
        {
            await ExecuteAsync(admin.ConnectionString, $"DROP USER [{reader}];");
            await ExecuteAsync(master.ConnectionString, $"DROP LOGIN [{reader}];");
        }
    }

    /// <summary>
    /// Grants a permission on a role of the test's database in a transaction that stays open, so the
    /// grant's catalog row and the role's permission metadata stay locked until the test ends it.
    /// </summary>
    private static async Task<OpenGrant> BeginConflictingGrantAsync(
        string databaseConnectionString,
        string role,
        string grantee)
    {
        var connection = await OpenAsync(databaseConnectionString);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"GRANT ALTER ON ROLE::[{role}] TO [{grantee}]; SELECT CAST(@@SPID AS smallint);";
        var sessionId = (short)(await command.ExecuteScalarAsync())!;

        return new OpenGrant(connection, transaction, sessionId);
    }

    private static async Task<DbConnection> OpenVerificationConnectionAsync(
        SqlServerDatabaseReadProvider provider,
        string readerConnectionString,
        DatabaseReadLimits limits)
    {
        var connection = provider.CreateConnection(readerConnectionString, Guid.NewGuid().ToString("N"), limits);
        await connection.OpenAsync();

        return connection;
    }

    /// <summary>
    /// Waits until the verification on the session waits for a lock, in an attempt that started
    /// after <paramref name="after"/> when it is given. The bound only matters when the test fails.
    /// </summary>
    private static async Task<BlockedAttempt> WaitForBlockedVerificationAsync(
        SqlConnection monitor,
        short sessionId,
        DateTime? after)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using (var command = monitor.CreateCommand())
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

    private static async Task<int> CountLocksInMasterAsync(SqlConnection monitor, short sessionId)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sys.dm_tran_locks
            WHERE request_session_id = @sessionId AND resource_database_id = 1
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<short> GetSessionIdAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(@@SPID AS smallint);";

        return (short)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        return connection;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record BlockedAttempt(DateTime StartTime, int DeadlockPriority);

    private sealed class OpenGrant(SqlConnection connection, SqlTransaction transaction, short sessionId)
        : IAsyncDisposable
    {
        public SqlTransaction Transaction { get; } = transaction;

        public short SessionId { get; } = sessionId;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
