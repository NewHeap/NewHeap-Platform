using AwesomeAssertions;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The <c>session</c> protocol, which needs no database: the ready line, what a line may look like,
/// the bounds of a session and how a request that fails before a connection leaves it usable.
/// </summary>
public sealed class DatabaseReadSessionTests
{
    private const string ReadyLine =
        """{"session":{"protocolVersion":1,"commands":["query","schema"],"schemaOperations":["search","describe","indexes","catalog"]}}""";

    private const string UnusedConnectionString = "Host=example.invalid;Database=example;Username=test;Password=test";

    [Fact]
    public async Task ReadyLineComesFirstAndTouchesNeitherTheProfileNorTheDatabase()
    {
        var factory = new RecordingConnectionFactory();
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["session", "--profiles", Path.Combine(Path.GetTempPath(), "does-not-exist", "catalog.json")],
            input,
            output,
            factory);

        exitCode.Should().Be(0);
        DatabaseReadTestWorkspace.OutputText(output).Should().Be(ReadyLine + "\n");
        factory.OpenCount.Should().Be(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"id":12,"command":"query","request":{}}""")]
    public async Task MalformedLineAnswersWithoutAnIdAndTheSessionGoesOn(string line)
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        using var answer = await session.SendLineAsync(line);

        answer.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        answer.RootElement.GetProperty("exitCode").GetInt32().Should().Be(2);
        var response = answer.RootElement.GetProperty("response");
        response.GetProperty("ok").GetBoolean().Should().BeFalse();
        response.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-json");

        await AssertSessionStillAnswersAsync(session);
    }

    [Theory]
    [InlineData("""{"command":"query","request":{}}""")]
    [InlineData("""{"id":"","command":"query","request":{}}""")]
    public async Task LineWithoutAnIdIsRefused(string line)
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        using var answer = await session.SendLineAsync(line);

        answer.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        answer.RootElement.GetProperty("exitCode").GetInt32().Should().Be(2);
        answer.RootElement.GetProperty("response").GetProperty("error").GetProperty("code").GetString()
            .Should().Be("invalid-session-id");
    }

    [Fact]
    public async Task IdKeepsItsBoundOfSixtyFourCharacters()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();
        var longest = new string('x', DatabaseReadSession.MaximumIdLength);

        using var accepted = await session.SendLineAsync($$$"""{"id":"{{{longest}}}","command":"unknown","request":{}}""");
        using var refused = await session.SendLineAsync($$$"""{"id":"{{{longest}}}y","command":"query","request":{}}""");

        accepted.RootElement.GetProperty("id").GetString().Should().Be(longest);
        refused.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("""{"id":"a","command":"validate","request":{}}""", "unknown-session-command")]
    [InlineData("""{"id":"a","command":"QUERY","request":{}}""", "unknown-session-command")]
    [InlineData("""{"id":"a","request":{}}""", "unknown-session-command")]
    [InlineData("""{"id":"a","command":"query"}""", "session-request-required")]
    public async Task ACommandOutsideTheContractKeepsItsIdAndIsRefused(string line, string code)
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        using var answer = await session.SendLineAsync(line);

        answer.RootElement.GetProperty("id").GetString().Should().Be("a");
        answer.RootElement.GetProperty("exitCode").GetInt32().Should().Be(2);
        answer.RootElement.GetProperty("response").GetProperty("error").GetProperty("code").GetString()
            .Should().Be(code);
    }

    [Fact]
    public async Task OversizedLineIsReadToItsEndAndTheNextLineIsServed()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        var send = session.WriteAsync(new string('x', DatabaseReadSession.MaximumLineBytes + 1) + "\n");
        using var answer = JsonDocument.Parse(await session.ReadLineAsync());
        await send;

        answer.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        answer.RootElement.GetProperty("exitCode").GetInt32().Should().Be(2);
        answer.RootElement.GetProperty("response").GetProperty("error").GetProperty("code").GetString()
            .Should().Be("request-too-large");

        await AssertSessionStillAnswersAsync(session);
    }

    [Fact]
    public async Task RequestBodyKeepsTheHardLimitOfAOneShotCommand()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();
        var padding = new string(' ', DatabaseReadJson.MaximumRequestBytes);

        var answer = await session.SendAsync("query", "{" + padding + "}");

        answer.ExitCode.Should().Be(2);
        answer.ErrorCode.Should().Be("request-too-large");
    }

    [Fact]
    public async Task ARequestFailsWithTheCodeAndExitCodeOfTheOneShotCommand()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var factory = new RecordingConnectionFactory();
        await using var session = new DatabaseReadSessionClient(workspace, factory);
        await session.ReadReadyLineAsync();

        var write = await session.QueryAsync("DELETE FROM projects");
        var unknownProperty = await session.SendAsync("query", """{"schemaVersion":1,"sqlText":"SELECT 1"}""");
        var catalogWithFilter = await session.SendAsync(
            "schema",
            """{"schemaVersion":1,"schema":{"operation":"catalog","searchTerm":"x"},"reason":"Test"}""");
        var wrongProfile = await session.SendAsync(
            "query",
            """{"schemaVersion":1,"profile":"other","sql":"SELECT 1","reason":"Test"}""");

        write.ExitCode.Should().Be(4);
        write.ErrorCode.Should().Be("query-not-read-only");
        unknownProperty.ExitCode.Should().Be(2);
        unknownProperty.ErrorCode.Should().Be("invalid-json");
        catalogWithFilter.ExitCode.Should().Be(2);
        catalogWithFilter.ErrorCode.Should().Be("invalid-schema-catalog");
        wrongProfile.ExitCode.Should().Be(3);
        wrongProfile.ErrorCode.Should().Be("profile-not-found");
        factory.OpenCount.Should().Be(0, "a refused request never reaches the database");
    }

    [Fact]
    public async Task AConnectionFailureAnswersTheRequestAndTheNextOneTriesAgain()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var factory = new RecordingConnectionFactory();
        await using var session = new DatabaseReadSessionClient(workspace, factory);
        await session.ReadReadyLineAsync();

        var first = await session.QueryAsync("SELECT 1");
        var second = await session.QueryAsync("SELECT 1");

        foreach (var answer in new[] { first, second })
        {
            answer.ExitCode.Should().Be(5);
            answer.ErrorCode.Should().Be("database-query-failed");
            answer.Classification.Should().Be("connection-failed");
            answer.Error.GetProperty("stage").GetString().Should().Be("connection-open");
            answer.Error.GetProperty("transient").GetBoolean().Should().BeTrue();
        }

        factory.OpenCount.Should().Be(2);
    }

    /// <summary>
    /// A session keeps the connection string it resolved, so what makes it read the configuration
    /// again is a connection that fails, as one with a rotated credential does.
    /// </summary>
    [Fact]
    public async Task AFailedConnectionMakesTheNextRequestResolveTheConfigurationAgain()
    {
        const string rotated = "Host=rotated.example.invalid;Database=example;Username=test;Password=test";
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var factory = new RecordingConnectionFactory();
        await using var session = new DatabaseReadSessionClient(workspace, factory);
        await session.ReadReadyLineAsync();

        await session.QueryAsync("SELECT 1");
        workspace.WriteEnvironmentConnectionString("Development", rotated);
        await session.QueryAsync("SELECT 1");

        factory.ConnectionKeys[0].Should().Contain("example.invalid").And.NotContain("rotated");
        factory.ConnectionKeys[1].Should().Contain("rotated.example.invalid");
    }

    /// <summary>
    /// The session's connection string is stable, so a connection cannot take its timeout from a
    /// request. The request's timeout still bounds the open, with the failure a one-shot command
    /// reports when its connect timeout expires.
    /// </summary>
    [Fact]
    public async Task ARequestWaitsForAConnectionNoLongerThanItsTimeout()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var clock = new DatabaseReadTestClock();
        var factory = new RecordingConnectionFactory { WaitForCancellation = true };
        await using var session = new DatabaseReadSessionClient(workspace, factory, clock);
        await session.ReadReadyLineAsync();

        var query = session.QueryAsync("SELECT 1", timeoutSeconds: 5);
        await WaitUntilAsync(() => factory.OpenCount == 1);
        clock.Advance(TimeSpan.FromSeconds(5));
        var answer = await query;

        answer.ExitCode.Should().Be(5);
        answer.Classification.Should().Be("connection-failed");
        answer.Error.GetProperty("stage").GetString().Should().Be("connection-open");
        factory.LastConnectionLimits!.TimeoutSeconds.Should().Be(30, "the pool's key does not follow a request");
    }

    [Fact]
    public async Task TheRequestsOfASessionShareOneConnectionIdentity()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var factory = new RecordingConnectionFactory();
        await using var session = new DatabaseReadSessionClient(workspace, factory);
        await session.ReadReadyLineAsync();

        await session.QueryAsync("SELECT 1", timeoutSeconds: 3);
        await session.QueryAsync("SELECT 1", timeoutSeconds: 7);

        factory.ConnectionKeys.Should().HaveCount(2);
        factory.ConnectionKeys.Distinct().Should().ContainSingle(
            "the connection string and the application name are the key of the driver's pool");
    }

    [Theory]
    [InlineData("Host=example.invalid;Pooling=false;No Reset On Close=true;Password=x")]
    public void ReusableConnectionStringOverridesWhatASessionDependsOnForPostgreSql(string connectionString)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(
            new PostgreSqlDatabaseReadProvider().CreateReusableConnectionString(connectionString));

        builder.Pooling.Should().BeTrue();
        builder.NoResetOnClose.Should().BeFalse();
    }

    [Fact]
    public void ReusableConnectionStringOverridesWhatASessionDependsOnForMySqlFamily()
    {
        var builder = new MySqlConnector.MySqlConnectionStringBuilder(
            new MySqlDatabaseReadProvider(DatabaseProviderKind.MySql).CreateReusableConnectionString(
                "Server=example.invalid;Pooling=false;Connection Reset=false;Password=x"));

        builder.Pooling.Should().BeTrue();
        builder.ConnectionReset.Should().BeTrue();
    }

    [Fact]
    public async Task AnInvalidConnectionStringIsRefusedWithoutItsValue()
    {
        const string canary = "NEVER-RETURN-THIS-VALUE";
        using var workspace = new DatabaseReadTestWorkspace("postgresql", $"Not A Keyword={canary}");
        await using var session = new DatabaseReadSessionClient(workspace);
        var output = new StringBuilder(ReadyLine);
        await session.ReadReadyLineAsync();

        var answer = await session.QueryAsync("SELECT 1");
        output.Append(answer.Text);

        answer.ExitCode.Should().Be(3);
        answer.ErrorCode.Should().Be("connection-configuration-invalid");
        output.ToString().Should().NotContain(canary);
    }

    [Fact]
    public async Task EndOfInputEndsTheSessionWithoutAnotherLine()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        var exitCode = await session.CompleteAsync();

        exitCode.Should().Be(0);
        (await session.ReadToEndAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LastLineWithoutANewlineIsStillAnswered()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        await session.WriteAsync("""{"id":"last","command":"unknown","request":{}}""");
        var exitCode = await session.CompleteAsync();

        exitCode.Should().Be(0);
        using var answer = JsonDocument.Parse(await session.ReadLineAsync());
        answer.RootElement.GetProperty("id").GetString().Should().Be("last");
    }

    [Fact]
    public async Task ByteOrderMarkInFrontOfTheFirstLineIsNoFailure()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();

        await session.WriteAsync("﻿" + """{"id":"first","command":"unknown","request":{}}""" + "\r\n");

        using var answer = JsonDocument.Parse(await session.ReadLineAsync());
        answer.RootElement.GetProperty("id").GetString().Should().Be("first");
    }

    [Fact]
    public async Task IdleSessionEndsCleanlyAfterItsBound()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var clock = new DatabaseReadTestClock();
        await using var session = new DatabaseReadSessionClient(
            workspace,
            timeProvider: clock,
            arguments: ["--maximum-idle-seconds", "120"]);
        await session.ReadReadyLineAsync();

        await WaitUntilAsync(() => clock.HasTimers);
        clock.Advance(TimeSpan.FromSeconds(119));
        session.Completion.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));

        (await session.Completion.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
    }

    [Fact]
    public async Task ARequestRestartsTheIdleTimeButNotTheLifetime()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var clock = new DatabaseReadTestClock();
        await using var session = new DatabaseReadSessionClient(
            workspace,
            timeProvider: clock,
            arguments: ["--maximum-idle-seconds", "100", "--maximum-lifetime-seconds", "150"]);
        await session.ReadReadyLineAsync();

        await WaitUntilAsync(() => clock.HasTimers);
        clock.Advance(TimeSpan.FromSeconds(90));
        await AssertSessionStillAnswersAsync(session);
        await WaitUntilAsync(() => clock.HasTimers);
        clock.Advance(TimeSpan.FromSeconds(59));
        session.Completion.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));

        (await session.Completion.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
    }

    [Fact]
    public async Task CancellationEndsAnIdleSessionWithTheCancelledExitCode()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        using var cancellation = new CancellationTokenSource();
        await using var session = new DatabaseReadSessionClient(workspace, cancellationToken: cancellation.Token);
        await session.ReadReadyLineAsync();

        await cancellation.CancelAsync();

        (await session.Completion.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(130);
    }

    [Fact]
    public async Task AnOutputThatCannotBeWrittenEndsTheSession()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var line = Encoding.UTF8.GetBytes("""{"id":"a","command":"unknown","request":{}}""" + "\n");
        using var input = new MemoryStream(line);
        using var output = new FailingAfterFirstWriteStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["session", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(1);
    }

    [Fact]
    public async Task AnOutputThatIsGoneBeforeTheReadyLineEndsTheSession()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        output.Dispose();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["session", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(1);
    }

    [Fact]
    public void SessionOptionsHaveDefaultsAndCeilings()
    {
        var defaults = CliOptions.Parse(["session"]).Session!;
        var chosen = CliOptions.Parse(
        [
            "session",
            "--maximum-lifetime-seconds",
            "7200",
            "--maximum-idle-seconds",
            "60",
            "--verification-interval-seconds",
            "30"
        ]).Session!;

        defaults.MaximumLifetime.Should().Be(TimeSpan.FromHours(1));
        defaults.MaximumIdleTime.Should().Be(TimeSpan.FromMinutes(5));
        defaults.VerificationInterval.Should().Be(TimeSpan.FromSeconds(60));
        chosen.MaximumLifetime.Should().Be(TimeSpan.FromHours(2));
        chosen.MaximumIdleTime.Should().Be(TimeSpan.FromMinutes(1));
        chosen.VerificationInterval.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData("invalid-argument-value", "session", "--maximum-lifetime-seconds", "86401")]
    [InlineData("invalid-argument-value", "session", "--maximum-idle-seconds", "0")]
    [InlineData("invalid-argument-value", "session", "--verification-interval-seconds", "3601")]
    [InlineData("session-arguments-require-session-command", "query", "--maximum-idle-seconds", "60")]
    [InlineData("request-file-not-supported", "session", "--request-file", "request.json")]
    [InlineData("schema-arguments-require-schema-command", "session", "--maximum-rows", "5")]
    public void ArgumentsOutsideTheSessionContractAreRefused(string code, params string[] arguments)
    {
        var parse = () => CliOptions.Parse(arguments);

        parse.Should().Throw<DatabaseReadExpectedException>().Where(exception => exception.Code == code);
    }

    /// <summary>
    /// The process itself: a caller sees the ready line without a catalog, and an idle session ends
    /// although nothing can cancel its read from the console stream.
    /// </summary>
    [Fact]
    public async Task ProcessPrintsTheReadyLineAndEndsWhenItWasIdleForItsBound()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", UnusedConnectionString);
        var toolAssemblyPath = typeof(NewHeapDatabaseReadApplication).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(toolAssemblyPath)!
        };
        startInfo.ArgumentList.Add(toolAssemblyPath);
        startInfo.ArgumentList.Add("session");
        startInfo.ArgumentList.Add("--profiles");
        startInfo.ArgumentList.Add(workspace.ProfileCatalogPath);
        startInfo.ArgumentList.Add("--maximum-idle-seconds");
        startInfo.ArgumentList.Add("1");

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first = await process!.StandardOutput.ReadLineAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);

        first.Should().Be(ReadyLine);
        process.ExitCode.Should().Be(0);
        (await process.StandardError.ReadToEndAsync(cancellation.Token)).Should().BeEmpty();
    }

    private static async Task AssertSessionStillAnswersAsync(DatabaseReadSessionClient session)
    {
        using var answer = await session.SendLineAsync("""{"id":"next","command":"unknown","request":{}}""");

        answer.RootElement.GetProperty("id").GetString().Should().Be("next");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class RecordingConnectionFactory : IDatabaseReadConnectionFactory
    {
        private int _openCount;

        public bool WaitForCancellation { get; init; }

        public int OpenCount => Volatile.Read(ref _openCount);

        public DatabaseReadLimits? LastConnectionLimits { get; private set; }

        public List<string> ConnectionKeys { get; } = [];

        public async Task<DbConnection> OpenAsync(
            IDatabaseReadProvider provider,
            string connectionString,
            string requestId,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            LastConnectionLimits = limits;
            await using var connection = provider.CreateConnection(connectionString, requestId, limits);
            ConnectionKeys.Add(connection.ConnectionString);
            Interlocked.Increment(ref _openCount);

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            throw new TestConnectionException();
        }
    }

    private sealed class TestConnectionException : DbException;

    private sealed class FailingAfterFirstWriteStream : MemoryStream
    {
        private int _flushCount;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return ++_flushCount > 1
                ? Task.FromException(new IOException("The reader went away."))
                : Task.CompletedTask;
        }
    }
}
