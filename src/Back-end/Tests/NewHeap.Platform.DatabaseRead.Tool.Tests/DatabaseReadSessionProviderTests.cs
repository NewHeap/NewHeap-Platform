using AwesomeAssertions;
using Microsoft.Data.SqlClient;
using NewHeap.Platform.Tests.Relational;
using System.Data.Common;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// A session on real servers: what a one-shot command guarantees holds per request, requests share
/// one verified physical connection, nothing a failed request did reaches the next one, and the
/// principal is verified again after the interval and after a reconnect. Every behavior is proved
/// on all four providers, as a theory over the relational providers and one over the MySQL flavors.
/// </summary>
public sealed class DatabaseReadSessionProviderTests
{
    private const string SyntaxErrorSql = "SELECT id FROM FROM session_items";
    private const string RestrictedSql = "SELECT id FROM session_restricted";
    private const string ItemsSql = "SELECT id, name FROM session_items ORDER BY id";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task RequestsShareOneVerifiedConnection(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveRequestsShareOneVerifiedConnectionAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task RequestsShareOneVerifiedConnectionOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveRequestsShareOneVerifiedConnectionAsync(database);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task FailedRequestsLeaveTheSessionUsableAndTheirStateBehind(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveFailedRequestsLeaveNothingBehindAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task FailedRequestsLeaveTheSessionUsableAndTheirStateBehindOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveFailedRequestsLeaveNothingBehindAsync(database);
    }

    /// <summary>
    /// A statement can leave state on a MySQL or MariaDB session without any write, as
    /// <c>LAST_INSERT_ID(expr)</c> does. The driver's reset takes it away between requests.
    /// </summary>
    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task SessionStateOfAStatementDoesNotReachTheNextRequest(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var session = await StartAsync(workspace);

        var set = await session.QueryAsync("SELECT LAST_INSERT_ID(42)");
        var read = await session.QueryAsync("SELECT LAST_INSERT_ID()");

        set.ExitCode.Should().Be(0, set.Text);
        set.FirstCell.GetRawText().Trim('"').Should().Be("42");
        read.ExitCode.Should().Be(0, read.Text);
        read.FirstCell.GetRawText().Trim('"').Should().Be("0");
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task VerificationRunsAgainAfterTheIntervalAndAfterAReconnect(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveVerificationRunsAgainAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task VerificationRunsAgainAfterTheIntervalAndAfterAReconnectOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveVerificationRunsAgainAsync(database);
    }

    /// <summary>
    /// A profile's connection string may turn pooling off, as the tests' MySQL accounts do. A
    /// session keeps its guarantees and its speed whatever it says.
    /// </summary>
    [Fact]
    public void ReusableConnectionStringOverridesWhatASessionDependsOnForSqlServer()
    {
        var builder = new SqlConnectionStringBuilder(
            new SqlServerDatabaseReadProvider().CreateReusableConnectionString(
                "Server=example.invalid;Pooling=false;ConnectRetryCount=3;Password=x;User ID=x"));

        builder.Pooling.Should().BeTrue();
        builder.ConnectRetryCount.Should().Be(0, "a hidden reconnect would skip the verification");
    }

    private static async Task ProveRequestsShareOneVerifiedConnectionAsync(DatabaseReadSessionDatabase database)
    {
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var connections = new PhysicalConnectionRecorder();
        await using var session = await StartAsync(workspace, connections);

        var items = await session.QueryAsync(ItemsSql);
        items.ExitCode.Should().Be(0, items.Text);
        var result = items.Response.GetProperty("result");
        result.GetProperty("rowCount").GetInt32().Should().Be(3);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
        items.Response.GetProperty("target").GetProperty("provider").GetString().Should().Be(database.ProviderName);
        items.Response.GetProperty("target").GetProperty("readOnlyVerified").GetBoolean().Should().BeTrue();

        var parameter = await session.QueryAsync(
            "SELECT name FROM session_items WHERE id = @id",
            parameters: [new { name = "id", type = "int32", value = 2 }]);
        parameter.ExitCode.Should().Be(0, parameter.Text);
        parameter.FirstCell.GetString().Should().Be("Two");

        (await session.SchemaAsync("search", searchTerm: "session_items")).ExitCode.Should().Be(0);
        (await session.SchemaAsync("describe", database.Schema, "session_items")).ExitCode.Should().Be(0);
        (await session.SchemaAsync("indexes", database.Schema, "session_items")).ExitCode.Should().Be(0);
        (await session.SchemaAsync("catalog", database.Schema)).ExitCode.Should().Be(0);
        (await session.QueryAsync(ItemsSql)).ExitCode.Should().Be(0);

        connections.Ids.Should().HaveCount(7);
        connections.Ids.Distinct().Should().ContainSingle("the requests of a session share one physical connection");
        (await session.CompleteAsync()).Should().Be(0);
    }

    private static async Task ProveFailedRequestsLeaveNothingBehindAsync(DatabaseReadSessionDatabase database)
    {
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var connections = new PhysicalConnectionRecorder();
        await using var session = await StartAsync(workspace, connections);

        async Task AssertRequestsSeeOnlyTheirOwnStateAsync()
        {
            foreach (var timeoutSeconds in new[] { 7, 5 })
            {
                var state = await session.QueryAsync(database.StateSql, timeoutSeconds: timeoutSeconds);
                state.ExitCode.Should().Be(0, state.Text);
                database.AssertRequestState(state.Response.GetProperty("result").GetProperty("rows")[0], timeoutSeconds);
            }
        }

        await AssertRequestsSeeOnlyTheirOwnStateAsync();

        var syntax = await session.QueryAsync(SyntaxErrorSql);
        syntax.ExitCode.Should().Be(5, syntax.Text);
        syntax.Classification.Should().Be("syntax-error");
        syntax.Error.GetProperty("stage").GetString().Should().Be("query-execution");
        await AssertRequestsSeeOnlyTheirOwnStateAsync();

        var permission = await session.QueryAsync(RestrictedSql);
        permission.ExitCode.Should().Be(5, permission.Text);
        permission.Classification.Should().Be("permission-denied");
        await AssertRequestsSeeOnlyTheirOwnStateAsync();

        var rows = await session.QueryAsync(ItemsSql, maximumRows: 1);
        rows.ExitCode.Should().Be(4, rows.Text);
        rows.ErrorCode.Should().Be("result-row-limit-exceeded");
        rows.Response.TryGetProperty("result", out _).Should().BeFalse("a row limit fails closed");
        await AssertRequestsSeeOnlyTheirOwnStateAsync();

        var write = await session.QueryAsync("DELETE FROM session_items");
        write.ExitCode.Should().Be(4, write.Text);
        write.ErrorCode.Should().Be("query-not-read-only");

        connections.Ids.Distinct().Should().ContainSingle("a failed request does not cost the connection");

        var slow = await session.QueryAsync(database.SlowSql, timeoutSeconds: 1);
        slow.ExitCode.Should().Be(5, slow.Text);
        slow.Error.GetProperty("stage").GetString().Should().Be("query-execution", slow.Text);
        slow.Classification.Should().Be("statement-timeout", slow.Text);
        await AssertRequestsSeeOnlyTheirOwnStateAsync();

        var items = await session.QueryAsync(ItemsSql);
        items.ExitCode.Should().Be(0, items.Text);
        items.Response.GetProperty("result").GetProperty("rowCount").GetInt32().Should().Be(3);
        (await session.CompleteAsync()).Should().Be(0);
    }

    /// <summary>
    /// A write grant shows when the verification ran: requests within the interval do not see it,
    /// the first one after it is refused, and so is the first one on a new connection. A refused
    /// request ends nothing: the session serves again once the grant is gone.
    /// </summary>
    private static async Task ProveVerificationRunsAgainAsync(DatabaseReadSessionDatabase database)
    {
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var clock = new DatabaseReadTestClock();
        var connections = new PhysicalConnectionRecorder();
        await using var session = await StartAsync(workspace, connections, clock);

        (await session.QueryAsync(ItemsSql)).ExitCode.Should().Be(0);
        await database.GrantWriteAsync();

        var withinInterval = await session.QueryAsync(ItemsSql);
        withinInterval.ExitCode.Should().Be(0, withinInterval.Text);

        clock.Advance(TimeSpan.FromSeconds(59));
        (await session.QueryAsync(ItemsSql)).ExitCode.Should().Be(0);

        clock.Advance(TimeSpan.FromSeconds(1));
        AssertRefused(await session.QueryAsync(ItemsSql), database);
        AssertRefused(await session.SchemaAsync("catalog"), database);

        await database.RevokeWriteAsync();
        var afterRevoke = await session.QueryAsync(ItemsSql);
        afterRevoke.ExitCode.Should().Be(0, afterRevoke.Text);
        connections.Ids[^1].Should().NotBe(
            connections.Ids[0],
            "a connection that failed its verification is not used again");

        var connectionId = await ConnectionIdAsync(session, database);
        await database.GrantWriteAsync();
        await database.EndConnectionAsync(connectionId);

        var first = await session.QueryAsync(ItemsSql);
        if (first.ExitCode == 5)
        {
            first.Classification.Should().Be("connection-failed", first.Text);
            first.Error.GetProperty("transient").GetBoolean().Should().BeTrue();
            first = await session.QueryAsync(ItemsSql);
        }

        AssertRefused(first, database);

        await database.RevokeWriteAsync();
        var served = await session.QueryAsync(ItemsSql);
        served.ExitCode.Should().Be(0, served.Text);

        await database.EndConnectionAsync(await ConnectionIdAsync(session, database));
        clock.Advance(TimeSpan.FromSeconds(60));
        var verifiedOnALostConnection = await session.QueryAsync(ItemsSql);
        if (verifiedOnALostConnection.ExitCode != 0)
        {
            verifiedOnALostConnection.Classification.Should().Be("connection-failed", verifiedOnALostConnection.Text);
        }

        (await session.QueryAsync(ItemsSql)).ExitCode.Should().Be(0);
        (await session.CompleteAsync()).Should().Be(0);
    }

    private static void AssertRefused(SessionAnswer answer, DatabaseReadSessionDatabase database)
    {
        answer.ExitCode.Should().Be(4, answer.Text);
        answer.ErrorCode.Should().Be("read-only-principal-not-verified");
        answer.Error.GetProperty("verificationCheck").GetString().Should().Be(database.WriteCheck);
        answer.Error.GetProperty("stage").GetString().Should().Be("readonly-verification");
    }

    private static async Task<DatabaseReadSessionClient> StartAsync(
        DatabaseReadTestWorkspace workspace,
        IDatabaseReadConnectionFactory? connections = null,
        TimeProvider? clock = null)
    {
        var session = new DatabaseReadSessionClient(workspace, connections, clock);
        using var ready = await session.ReadReadyLineAsync();
        ready.RootElement.GetProperty("session").GetProperty("protocolVersion").GetInt32().Should().Be(1);

        return session;
    }

    private static async Task<string> ConnectionIdAsync(
        DatabaseReadSessionClient session,
        DatabaseReadSessionDatabase database)
    {
        var answer = await session.QueryAsync(database.ConnectionIdSql);
        answer.ExitCode.Should().Be(0, answer.Text);

        return answer.FirstCell.ValueKind == JsonValueKind.String
            ? answer.FirstCell.GetString()!
            : answer.FirstCell.GetRawText();
    }

    /// <summary>
    /// Records the identifier the tool itself goes by for each connection it gets. On SQL Server the
    /// server's own number says nothing: a new connection gets the number of the one that just ended.
    /// </summary>
    private sealed class PhysicalConnectionRecorder : IDatabaseReadConnectionFactory
    {
        public List<string> Ids { get; } = [];

        public async Task<DbConnection> OpenAsync(
            IDatabaseReadProvider provider,
            string connectionString,
            string requestId,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            var connection = await DatabaseReadConnectionFactory.Instance.OpenAsync(
                provider,
                connectionString,
                requestId,
                limits,
                cancellationToken);
            Ids.Add(provider.GetPhysicalConnectionId(connection));

            return connection;
        }
    }
}
