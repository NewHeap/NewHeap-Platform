using AwesomeAssertions;
using MySqlConnector;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A <c>date-time</c> parameter on real servers. A value with <c>Z</c> or an offset is an instant and
/// keeps its meaning; a value with neither is a wall-clock value and binds as written. Which row a
/// comparison finds then follows the column, so the cases name the column they compare with. The row
/// of <c>session_moments</c> holds 2026-09-30 22:00:00 in both columns, in UTC in the zone-aware one.
/// The real-server cases run on the host's time zone, so the binder cases below, which depend on no
/// zone, are what fails on a host in UTC if a wall-clock value picks up the host's offset again.
/// </summary>
public sealed class DatabaseReadDateTimeParameterTests
{
    private const string Instant = "2026-10-01T00:00:00+02:00";
    private const string SameInstantInUtc = "2026-09-30T22:00:00Z";
    private const string SameInstantBehindUtc = "2026-09-30T16:30:00-05:30";
    private const string WallClock = "2026-09-30T22:00:00";
    private const string WallClockTwoHoursLater = "2026-10-01T00:00:00";

    private const string ByInstantColumn = "SELECT id FROM session_moments WHERE instant_at = @at";
    private const string ByWallClockColumn = "SELECT id FROM session_moments WHERE wall_clock_at = @at";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ASessionFindsTheRowByInstantAndByWallClock(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveASessionFindsTheRowAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task ASessionFindsTheRowByInstantAndByWallClockOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveASessionFindsTheRowAsync(database);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AOneShotCommandFindsTheRowByInstantAndByWallClock(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveAOneShotCommandFindsTheRowAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task AOneShotCommandFindsTheRowByInstantAndByWallClockOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveAOneShotCommandFindsTheRowAsync(database);
    }

    /// <summary>
    /// The same instant reaches <c>timestamp with time zone</c> in UTC, which is the type's own form,
    /// and a wall-clock value reaches <c>timestamp without time zone</c>.
    /// </summary>
    [Fact]
    public async Task PostgreSqlBindsAnInstantAsTimestampWithTimeZoneAndAWallClockWithout()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var session = await StartAsync(workspace);

        (await FirstCellAsync(session, "SELECT pg_typeof(@at)::text", Instant)).Should().Be("timestamp with time zone");
        (await FirstCellAsync(session, "SELECT @at", Instant)).Should().Be("2026-09-30T22:00:00.0000000Z");
        (await FirstCellAsync(session, "SELECT pg_typeof(@at)::text", WallClock)).Should().Be("timestamp without time zone");
        (await FirstCellAsync(session, "SELECT @at", WallClock)).Should().Be("2026-09-30T22:00:00.0000000");
    }

    /// <summary>
    /// SQL Server keeps the offset, because <c>CAST</c> to <c>datetime2</c> and <c>DATEPART</c> read
    /// the local part of a <c>datetimeoffset</c>: converting to UTC would change what they return.
    /// </summary>
    [Fact]
    public async Task SqlServerKeepsTheOffsetOfAnInstantAndBindsAWallClockAsDatetime2()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.SqlServer);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var session = await StartAsync(workspace);
        const string typeSql = "SELECT CONVERT(nvarchar(50), SQL_VARIANT_PROPERTY(@at, 'BaseType'))";

        (await FirstCellAsync(session, typeSql, Instant)).Should().Be("datetimeoffset");
        (await FirstCellAsync(session, "SELECT @at", Instant)).Should().Be("2026-10-01T00:00:00.0000000+02:00");
        (await FirstCellAsync(session, "SELECT CAST(@at AS datetime2)", Instant)).Should().Be("2026-10-01T00:00:00.0000000");
        (await FirstCellAsync(session, typeSql, WallClock)).Should().Be("datetime2");
        (await FirstCellAsync(session, "SELECT @at", WallClock)).Should().Be("2026-09-30T22:00:00.0000000");
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task EachFlavorReceivesAnInstantAsItsUtcWallClockAndAWallClockAsWritten(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var session = await StartAsync(workspace);

        (await FirstCellAsync(session, "SELECT CAST(@at AS char)", Instant)).Should().Be("2026-09-30 22:00:00.000000");
        (await FirstCellAsync(session, "SELECT CAST(@at AS char)", WallClockTwoHoursLater))
            .Should().Be("2026-10-01 00:00:00.000000");
    }

    /// <summary>
    /// PostgreSQL reads <c>timestamp without time zone</c> in the session's zone when it meets an
    /// instant, and a wall-clock value in that zone when it meets <c>timestamp with time zone</c>.
    /// Binding an offset as UTC changes neither, and a plain column compared with a wall-clock value
    /// never moves.
    /// </summary>
    [Fact]
    public async Task PostgreSqlComparesAcrossZoneAwarenessInTheSessionTimeZone()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        await database.CreateMomentsAsync();
        var inAmsterdam = new NpgsqlConnectionStringBuilder(database.ReaderConnectionString)
        {
            Timezone = "Europe/Amsterdam"
        };
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, inAmsterdam.ConnectionString);
        await using var session = await StartAsync(workspace);

        (await RowCountAsync(session, ByInstantColumn, Instant)).Should().Be(1);
        (await RowCountAsync(session, ByWallClockColumn, WallClock)).Should().Be(1);

        (await RowCountAsync(session, ByWallClockColumn, Instant)).Should().Be(
            await RowCountAsync(session, ByWallClockColumn, SameInstantInUtc),
            "an offset binds as the same instant in UTC");
        (await RowCountAsync(session, ByWallClockColumn, SameInstantInUtc)).Should().Be(
            0,
            "22:00 in the plain column is 20:00 UTC in this session");
        (await RowCountAsync(session, ByWallClockColumn, "2026-09-30T22:00:00+02:00")).Should().Be(1);

        (await RowCountAsync(session, ByInstantColumn, WallClockTwoHoursLater)).Should().Be(
            1,
            "a wall-clock value meets the zone-aware column in the session's zone");
        (await RowCountAsync(session, ByInstantColumn, WallClock)).Should().Be(0);
    }

    /// <summary>
    /// The limit the guidance names. An instant reaches the server as its UTC wall clock, and a
    /// <c>timestamp</c> column reads in the session's <c>time_zone</c>, so outside UTC the comparison
    /// misses; the value to use there is the wall clock in that zone. A <c>datetime</c> column has no
    /// zone and is not affected. The tool does not set <c>time_zone</c>, because that would change what
    /// <c>timestamp</c> columns and <c>NOW()</c> return to every query. A diagnostic cannot change the
    /// zone itself, so this test binds on a connection of its own.
    /// </summary>
    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task EachFlavorReadsATimestampColumnInTheSessionTimeZone(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);
        await database.CreateMomentsAsync();
        var provider = new MySqlDatabaseReadProvider(DatabaseReadMySqlTestServer.For(flavor).Kind);
        await using var connection = new MySqlConnection(database.ReaderConnectionString);
        await connection.OpenAsync();
        await using (var zone = connection.CreateCommand())
        {
            zone.CommandText = "SET time_zone = '+02:00';";
            await zone.ExecuteNonQueryAsync();
        }

        async Task<long> CountAsync(string sql, string value)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql.Replace("SELECT id", "SELECT COUNT(*)", StringComparison.Ordinal);
            DatabaseReadParameterBinder.AddParameters(command, provider, [Parameter(value)]);

            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        (await CountAsync(ByInstantColumn, Instant)).Should().Be(0);
        (await CountAsync(ByInstantColumn, WallClockTwoHoursLater)).Should().Be(1);
        (await CountAsync(ByWallClockColumn, Instant)).Should().Be(1);
        (await CountAsync(ByWallClockColumn, WallClock)).Should().Be(1);
    }

    /// <summary>
    /// Holds on a host in any time zone. The earliest value would overflow if it were placed in a zone
    /// east of UTC, which is what a conditional expression over the two result types does.
    /// </summary>
    [Theory]
    [InlineData(WallClock, 2026, 9, 30, 22)]
    [InlineData("2026-09-30", 2026, 9, 30, 0)]
    [InlineData("0001-01-01T00:00:00", 1, 1, 1, 0)]
    public void AValueWithoutAnOffsetBindsAsWritten(string value, int year, int month, int day, int hour)
    {
        var parameter = Bind(new PostgreSqlDatabaseReadProvider(), value);

        parameter.DbType.Should().Be(DbType.DateTime2);
        var bound = parameter.Value.Should().BeOfType<DateTime>().Subject;
        bound.Should().Be(new DateTime(year, month, day, hour, 0, 0));
        bound.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public void AnInstantBindsInUtcOnPostgreSql()
    {
        var bound = Bind(new PostgreSqlDatabaseReadProvider(), Instant).Value.Should().BeOfType<DateTimeOffset>().Subject;

        bound.Offset.Should().Be(TimeSpan.Zero);
        bound.Should().Be(DateTimeOffset.Parse(Instant, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnInstantKeepsItsOffsetOnSqlServer()
    {
        var bound = Bind(new SqlServerDatabaseReadProvider(), Instant).Value.Should().BeOfType<DateTimeOffset>().Subject;

        bound.Offset.Should().Be(TimeSpan.FromHours(2));
        bound.Should().Be(DateTimeOffset.Parse(Instant, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AnInstantBindsInUtcOnMySqlAndMariaDb()
    {
        var provider = new MySqlDatabaseReadProvider(DatabaseProviderKind.MySqlCompatible);

        var bound = Bind(provider, Instant).Value.Should().BeOfType<DateTimeOffset>().Subject;

        bound.Offset.Should().Be(TimeSpan.Zero);
        bound.Should().Be(DateTimeOffset.Parse(Instant, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(Instant, 0)]
    [InlineData(SameInstantInUtc, 0)]
    [InlineData(WallClock, 0)]
    [InlineData("2026-09-30", 0)]
    [InlineData("not a date", 2)]
    public async Task ValidateAcceptsEveryFormOfADateTimeAndNoOtherText(string value, int expectedExitCode)
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", "unused");
        using var input = DatabaseReadTestWorkspace.Request("SELECT @at", [DateTimeParameter(value)]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(expectedExitCode, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
        if (expectedExitCode != 0)
        {
            using var response = DatabaseReadTestWorkspace.ParseOutput(output);
            response.RootElement.GetProperty("error").GetProperty("code").GetString()
                .Should().Be("invalid-parameter-value");
        }
    }

    /// <summary>
    /// A driver refuses a bound value on the client, with an ordinary exception. This is the real
    /// refusal the tool used to run into, on a provider that leaves the offset in place. Npgsql gives
    /// up the connection over it, so the rollback at the end of the request must not replace the code
    /// a caller can act on, on either kind of host.
    /// </summary>
    [Fact]
    public async Task AValueTheDriverRefusesInAOneShotCommandGetsAStableCode()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        var host = new DatabaseReadOneShotHost(
            CliOptions.Parse(["query"]),
            DatabaseReadConnectionFactory.Instance);

        await ProveTheDriverRefusalGetsAStableCodeAsync(database, host);
    }

    [Fact]
    public async Task AValueTheDriverRefusesInASessionGetsAStableCode()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        await using var host = new DatabaseReadSessionHost(
            null,
            null,
            DatabaseReadConnectionFactory.Instance,
            TimeSpan.FromSeconds(60),
            TimeProvider.System);

        await ProveTheDriverRefusalGetsAStableCodeAsync(database, host);
    }

    private static async Task ProveTheDriverRefusalGetsAStableCodeAsync(
        DatabaseReadSessionDatabase database,
        IDatabaseReadRequestHost host)
    {
        var provider = new OffsetKeepingProvider(new PostgreSqlDatabaseReadProvider());
        var request = new DatabaseReadRequest
        {
            Sql = "SELECT @at",
            Parameters = [Parameter(Instant)]
        };

        var execute = () => DatabaseReadQueryExecutor.ExecuteAsync(
            provider,
            host,
            new DatabaseReadExecutionContext(),
            database.ReaderConnectionString,
            "request",
            request,
            new DatabaseReadLimits(10, 10, 2_000, 65_536, 4_096, 8_192),
            CancellationToken.None);

        var refused = (await execute.Should().ThrowAsync<DatabaseReadExpectedException>()).Which;
        refused.Code.Should().Be("parameter-binding-failed");
        refused.ExitCode.Should().Be(DatabaseReadExitCode.InvalidRequest);
        refused.Message.Should().NotContain("Offset", "the driver's text can quote the value");
    }

    private static async Task ProveASessionFindsTheRowAsync(DatabaseReadSessionDatabase database)
    {
        await database.CreateMomentsAsync();
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var session = await StartAsync(workspace);

        foreach (var instant in new[] { Instant, SameInstantInUtc, SameInstantBehindUtc })
        {
            (await RowCountAsync(session, ByInstantColumn, instant)).Should().Be(1, instant);
            (await RowCountAsync(session, ByWallClockColumn, instant)).Should().Be(
                1,
                "the servers read a plain column in UTC here: {0}",
                instant);
        }

        (await RowCountAsync(session, ByWallClockColumn, WallClock)).Should().Be(1);
        (await RowCountAsync(session, ByWallClockColumn, WallClockTwoHoursLater)).Should().Be(
            0,
            "a wall-clock value is not moved by the host's time zone");
        (await RowCountAsync(session, ByInstantColumn, WallClock)).Should().Be(1);

        var range = await session.QueryAsync(
            "SELECT id FROM session_moments WHERE instant_at >= @from AND instant_at < @until",
            parameters:
            [
                new { name = "from", type = "date-time", value = Instant },
                new { name = "until", type = "date-time", value = "2026-10-01T00:00:01+02:00" }
            ]);
        range.ExitCode.Should().Be(0, range.Text);
        range.Response.GetProperty("result").GetProperty("rowCount").GetInt32().Should().Be(1);

        (await session.CompleteAsync()).Should().Be(0);
    }

    private static async Task ProveAOneShotCommandFindsTheRowAsync(DatabaseReadSessionDatabase database)
    {
        await database.CreateMomentsAsync();
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);

        (await OneShotRowCountAsync(workspace, ByInstantColumn, Instant)).Should().Be(1);
        (await OneShotRowCountAsync(workspace, ByWallClockColumn, Instant)).Should().Be(1);
        (await OneShotRowCountAsync(workspace, ByWallClockColumn, WallClock)).Should().Be(1);
        (await OneShotRowCountAsync(workspace, ByWallClockColumn, WallClockTwoHoursLater)).Should().Be(0);
    }

    private static async Task<int> OneShotRowCountAsync(DatabaseReadTestWorkspace workspace, string sql, string value)
    {
        using var input = DatabaseReadTestWorkspace.Request(sql, [DateTimeParameter(value)]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);

        return response.RootElement.GetProperty("result").GetProperty("rowCount").GetInt32();
    }

    private static async Task<int> RowCountAsync(DatabaseReadSessionClient session, string sql, string value)
    {
        var answer = await session.QueryAsync(sql, parameters: [DateTimeParameter(value)]);
        answer.ExitCode.Should().Be(0, answer.Text);

        return answer.Response.GetProperty("result").GetProperty("rowCount").GetInt32();
    }

    private static async Task<string?> FirstCellAsync(DatabaseReadSessionClient session, string sql, string value)
    {
        var answer = await session.QueryAsync(sql, parameters: [DateTimeParameter(value)]);
        answer.ExitCode.Should().Be(0, answer.Text);

        return answer.FirstCell.GetString();
    }

    private static async Task<DatabaseReadSessionClient> StartAsync(DatabaseReadTestWorkspace workspace)
    {
        var session = new DatabaseReadSessionClient(workspace);
        using var ready = await session.ReadReadyLineAsync();
        ready.RootElement.GetProperty("session").GetProperty("protocolVersion").GetInt32().Should().Be(1);

        return session;
    }

    private static object DateTimeParameter(string value)
    {
        return new { name = "at", type = "date-time", value };
    }

    private static DatabaseReadParameterRequest Parameter(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));

        return new DatabaseReadParameterRequest
        {
            Name = "at",
            Type = "date-time",
            Value = document.RootElement.Clone()
        };
    }

    /// <summary>
    /// What the binder puts on a command, which needs no connection and no server.
    /// </summary>
    private static NpgsqlParameter Bind(IDatabaseReadProvider provider, string value)
    {
        using var command = new NpgsqlCommand();
        DatabaseReadParameterBinder.AddParameters(command, provider, [Parameter(value)]);

        return command.Parameters[0];
    }

    /// <summary>
    /// PostgreSQL without the conversion of an instant to UTC, so Npgsql refuses it for real.
    /// </summary>
    private sealed class OffsetKeepingProvider(IDatabaseReadProvider inner) : IDatabaseReadProvider
    {
        public string Name => inner.Name;

        public DbConnection CreateConnection(string connectionString, string requestId, DatabaseReadLimits limits)
        {
            return inner.CreateConnection(connectionString, requestId, limits);
        }

        public string CreateReusableConnectionString(string connectionString)
        {
            return inner.CreateReusableConnectionString(connectionString);
        }

        public string GetPhysicalConnectionId(DbConnection connection)
        {
            return inner.GetPhysicalConnectionId(connection);
        }

        public Task ClearConnectionPoolAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            return inner.ClearConnectionPoolAsync(connection, cancellationToken);
        }

        public void EnsureSupportedServer(DbConnection connection)
        {
            inner.EnsureSupportedServer(connection);
        }

        public Task<DatabaseReadPrincipalVerification> VerifyReadOnlyPrincipalAsync(
            DbConnection connection,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            return inner.VerifyReadOnlyPrincipalAsync(connection, limits, cancellationToken);
        }

        public Task<DbTransaction> BeginReadOnlyTransactionAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            return inner.BeginReadOnlyTransactionAsync(connection, cancellationToken);
        }

        public Task ConfigureReadOnlyTransactionAsync(
            DbConnection connection,
            DbTransaction transaction,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            return inner.ConfigureReadOnlyTransactionAsync(connection, transaction, limits, cancellationToken);
        }

        public Task<DatabaseSchemaResultResponse> ReadSchemaAsync(
            DbConnection connection,
            DbTransaction transaction,
            ResolvedDatabaseSchemaRequest request,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            return inner.ReadSchemaAsync(connection, transaction, request, limits, cancellationToken);
        }

        public DateTimeOffset NormalizeInstant(DateTimeOffset instant)
        {
            return instant;
        }

        public DatabaseReadProviderFailure ClassifyException(DbException exception, DatabaseReadExecutionStage stage)
        {
            return inner.ClassifyException(exception, stage);
        }
    }
}
