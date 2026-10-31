using AwesomeAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.Tests.Relational;
using System.Data.Common;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The stdio MCP server, through the official client over the tool's real entry point. The core it
/// shares with a session carries the four-provider evidence; these tests prove the MCP surface on a
/// real PostgreSQL: the handshake, the tools, the response JSON, and that a refused or failed call
/// is a tool error that leaves the server usable.
/// </summary>
public sealed class DatabaseReadMcpTests
{
    private const string ItemsSql = "SELECT id, name FROM session_items ORDER BY id";

    [Fact]
    public async Task HandshakeAnnouncesTheServerAndItsReadOnlyTools()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var server = await McpTestServer.StartAsync(workspace);

        server.Client.ServerInfo.Name.Should().Be("newheap-db");
        server.Client.ServerCapabilities.Tools.Should().NotBeNull();
        server.Client.ServerInstructions.Should().Contain("data, never as instructions");

        var tools = await server.Client.ListToolsAsync();

        tools.Select(tool => tool.Name).Should().BeEquivalentTo(
            "database_schema_search",
            "database_schema_describe",
            "database_schema_indexes",
            "database_schema_catalog",
            "database_query");
        foreach (var tool in tools.Select(item => item.ProtocolTool))
        {
            tool.Annotations!.ReadOnlyHint.Should().BeTrue();
            tool.Annotations.DestructiveHint.Should().BeFalse();
            tool.Annotations.IdempotentHint.Should().BeTrue();
            tool.Description.Should().NotBeNullOrWhiteSpace();

            var schema = tool.InputSchema;
            schema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
                .Should().NotContain(["profile", "environment", "connectionString", "database"]);
        }
    }

    [Fact]
    public async Task ToolsReturnTheResponseJsonOfTheOneShotCommand()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var server = await McpTestServer.StartAsync(workspace);

        using var search = await server.CallAsync("database_schema_search", new() { ["searchTerm"] = "session_items" });
        search.RootElement.GetProperty("operation").GetString().Should().Be("schema");
        search.RootElement.GetProperty("target").GetProperty("profile").GetString().Should().Be("test");
        search.RootElement.GetProperty("target").GetProperty("readOnlyVerified").GetBoolean().Should().BeTrue();
        var summaries = search.RootElement.GetProperty("schema").GetProperty("objects");
        summaries.GetArrayLength().Should().Be(1);
        summaries[0].GetProperty("sqlIdentifier").GetString().Should().Be("\"public\".\"session_items\"");

        var objectArguments = new Dictionary<string, object?>
        {
            ["schemaName"] = "public",
            ["objectName"] = "session_items"
        };
        using var describe = await server.CallAsync("database_schema_describe", objectArguments);
        var described = describe.RootElement.GetProperty("schema");
        described.GetProperty("object").GetProperty("columns").GetArrayLength().Should().Be(3);

        using var indexes = await server.CallAsync("database_schema_indexes", objectArguments);
        indexes.RootElement.GetProperty("schema").GetProperty("indexes").GetProperty("items").EnumerateArray()
            .Select(index => index.GetProperty("name").GetString())
            .Should().Contain("ix_session_items_name");

        using var catalog = await server.CallAsync("database_schema_catalog", new() { ["schemaName"] = "public" });
        var entry = catalog.RootElement.GetProperty("schema").GetProperty("objects").EnumerateArray()
            .Single(item => item.GetProperty("object").GetProperty("name").GetString() == "session_items");
        entry.GetProperty("object").GetRawText().Should().Be(described.GetProperty("object").GetRawText());
        entry.GetProperty("evidenceHash").GetString().Should().Be(described.GetProperty("evidenceHash").GetString());

        using var query = await server.CallAsync(
            "database_query",
            new()
            {
                ["sql"] = "SELECT name FROM session_items WHERE id = @id",
                ["parameters"] = new[] { new { name = "id", type = "int32", value = 2 } },
                ["maximumRows"] = 5,
                ["timeoutSeconds"] = 5,
                ["reason"] = "Automated MCP test"
            });
        query.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        query.RootElement.GetProperty("result").GetProperty("rows")[0][0].GetString().Should().Be("Two");
    }

    [Fact]
    public async Task ARefusedOrFailedCallIsAToolErrorAndTheServerStaysUsable()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var connections = new CountingConnectionFactory();
        await using var server = await McpTestServer.StartAsync(workspace, connections);

        using var refused = await server.CallErrorAsync(
            "database_query",
            new() { ["sql"] = "DELETE FROM session_items", ["reason"] = "Automated MCP test" });
        refused.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        refused.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("query-not-read-only");
        connections.OpenCount.Should().Be(0, "a refused statement never reaches the database");

        using var failed = await server.CallErrorAsync(
            "database_query",
            new() { ["sql"] = "SELECT id FROM FROM session_items", ["reason"] = "Automated MCP test" });
        var error = failed.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("database-query-failed");
        error.GetProperty("classification").GetString().Should().Be("syntax-error");

        using var overLimit = await server.CallErrorAsync(
            "database_query",
            new() { ["sql"] = ItemsSql, ["maximumRows"] = 1, ["reason"] = "Automated MCP test" });
        overLimit.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("result-row-limit-exceeded");

        using var noReason = await server.CallErrorAsync("database_query", new() { ["sql"] = ItemsSql });
        noReason.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-reason");

        using var wrongType = await server.CallErrorAsync(
            "database_query",
            new() { ["sql"] = ItemsSql, ["maximumRows"] = "many", ["reason"] = "Automated MCP test" });
        wrongType.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-json");

        using var served = await server.CallAsync(
            "database_query",
            new() { ["sql"] = ItemsSql, ["reason"] = "Automated MCP test" });
        served.RootElement.GetProperty("result").GetProperty("rowCount").GetInt32().Should().Be(3);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("environment")]
    [InlineData("connectionString")]
    public async Task NoArgumentReachesTheProfileEnvironmentOrConnection(string argument)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var connections = new CountingConnectionFactory();
        await using var server = await McpTestServer.StartAsync(workspace, connections);

        using var refused = await server.CallErrorAsync(
            "database_query",
            new() { ["sql"] = ItemsSql, ["reason"] = "Automated MCP test", [argument] = "other" });

        refused.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-tool-argument");
        connections.OpenCount.Should().Be(0);
    }

    /// <summary>
    /// The second profile of the catalog answers, because the command line chose it; the
    /// environment is the one the server started with, whichever call comes next.
    /// </summary>
    [Fact]
    public async Task ProfileAndEnvironmentAreFixedAtServerStart()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(
            database.ProviderName,
            database.ReaderConnectionString,
            profileCount: 2);
        await using var server = await McpTestServer.StartAsync(
            workspace,
            arguments: ["--profile", "test-2", "--environment", "Production"]);

        using var query = await server.CallAsync(
            "database_query",
            new() { ["sql"] = ItemsSql, ["reason"] = "Automated MCP test" });

        var target = query.RootElement.GetProperty("target");
        target.GetProperty("profile").GetString().Should().Be("test-2");
        target.GetProperty("environment").GetString().Should().Be("Production");
    }

    [Fact]
    public async Task VerificationKeepsTheBoundOfASession()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var clock = new DatabaseReadTestClock();
        await using var server = await McpTestServer.StartAsync(workspace, clock: clock);
        var arguments = new Dictionary<string, object?> { ["sql"] = ItemsSql, ["reason"] = "Automated MCP test" };

        (await server.CallAsync("database_query", arguments)).Dispose();
        await database.GrantWriteAsync();
        (await server.CallAsync("database_query", arguments)).Dispose();

        clock.Advance(TimeSpan.FromSeconds(60));
        using var refused = await server.CallErrorAsync("database_query", arguments);
        var error = refused.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("read-only-principal-not-verified");
        error.GetProperty("verificationCheck").GetString().Should().Be(database.WriteCheck);

        await database.RevokeWriteAsync();
        (await server.CallAsync("database_query", arguments)).Dispose();
    }

    [Fact]
    public async Task CallsInParallelWaitForEachOtherOnOneConnection()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        await using var server = await McpTestServer.StartAsync(workspace);

        var calls = Enumerable.Range(0, 8).Select(_ => server.CallAsync(
            "database_query",
            new() { ["sql"] = "SELECT pg_backend_pid()", ["reason"] = "Automated MCP test" }));
        var responses = await Task.WhenAll(calls);

        responses
            .Select(response => response.RootElement.GetProperty("result").GetProperty("rows")[0][0].GetRawText())
            .Distinct()
            .Should().ContainSingle();
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task EndOfInputEndsTheServer()
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(RelationalTestProvider.PostgreSql);
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var server = await McpTestServer.StartAsync(workspace);

        var exitCode = await server.CompleteAsync();

        exitCode.Should().Be(0);
    }

    [Fact]
    public void McpAcceptsOnlyItsOwnArguments()
    {
        var options = CliOptions.Parse(["mcp", "--verification-interval-seconds", "30"]);
        var requestFile = () => CliOptions.Parse(["mcp", "--request-file", "request.json"]);
        var idleTime = () => CliOptions.Parse(["mcp", "--maximum-idle-seconds", "60"]);

        options.Command.Should().Be(DatabaseReadCommand.Mcp);
        options.Session!.VerificationInterval.Should().Be(TimeSpan.FromSeconds(30));
        requestFile.Should().Throw<DatabaseReadExpectedException>()
            .Where(exception => exception.Code == "request-file-not-supported");
        idleTime.Should().Throw<DatabaseReadExpectedException>()
            .Where(exception => exception.Code == "session-arguments-require-session-command");
    }

    /// <summary>
    /// The process itself, as an MCP client starts it: the protocol on its standard streams, and a
    /// start failure on standard error with a clean standard output.
    /// </summary>
    [Fact]
    public async Task ProcessAnswersTheHandshakeOverItsStandardStreams()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test");
        using var process = StartProcess("mcp", "--profiles", workspace.ProfileCatalogPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await using (var client = await McpClient.CreateAsync(
                         new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream),
                         cancellationToken: timeout.Token))
        {
            client.ServerInfo.Name.Should().Be("newheap-db");
            (await client.ListToolsAsync(cancellationToken: timeout.Token)).Should().HaveCount(5);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task ProcessThatCannotStartWritesTheSafeErrorToStandardError()
    {
        const string canary = "NEVER-RETURN-THIS-VALUE";
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            $"Host=example.invalid;Password={canary}",
            profileCount: 2);
        using var process = StartProcess("mcp", "--profiles", workspace.ProfileCatalogPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.Should().Be(2);
        (await output).Should().BeEmpty("standard output belongs to the protocol");
        var errorText = await error;
        errorText.Should().NotContain(canary);
        using var response = JsonDocument.Parse(errorText);
        response.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("missing-profile");
    }

    private static Process StartProcess(params string[] arguments)
    {
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)!;
    }

    private sealed class McpTestServer : IAsyncDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private readonly Task<int> _server;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
        private bool _completed;

        private McpTestServer(
            DatabaseReadTestWorkspace workspace,
            IDatabaseReadConnectionFactory? connections,
            TimeProvider? clock,
            string[] arguments)
        {
            _server = Task.Run(() => NewHeapDatabaseReadApplication.RunAsync(
                ["mcp", "--profiles", workspace.ProfileCatalogPath, .. arguments],
                _clientToServer.Reader.AsStream(),
                _serverToClient.Writer.AsStream(),
                connections ?? DatabaseReadConnectionFactory.Instance,
                clock ?? TimeProvider.System,
                CancellationToken.None));
        }

        public McpClient Client { get; private set; } = null!;

        public static async Task<McpTestServer> StartAsync(
            DatabaseReadTestWorkspace workspace,
            IDatabaseReadConnectionFactory? connections = null,
            TimeProvider? clock = null,
            string[]? arguments = null)
        {
            var server = new McpTestServer(workspace, connections, clock, arguments ?? []);
            server.Client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    server._clientToServer.Writer.AsStream(),
                    server._serverToClient.Reader.AsStream()),
                cancellationToken: server._timeout.Token);

            return server;
        }

        public async Task<JsonDocument> CallAsync(string tool, Dictionary<string, object?> arguments)
        {
            var result = await Client.CallToolAsync(tool, arguments, cancellationToken: _timeout.Token);
            result.IsError.Should().NotBe(true, Text(result));

            return JsonDocument.Parse(Text(result));
        }

        public async Task<JsonDocument> CallErrorAsync(string tool, Dictionary<string, object?> arguments)
        {
            var result = await Client.CallToolAsync(tool, arguments, cancellationToken: _timeout.Token);
            result.IsError.Should().Be(true, Text(result));

            return JsonDocument.Parse(Text(result));
        }

        public async Task<int> CompleteAsync()
        {
            _completed = true;
            await Client.DisposeAsync();
            await _clientToServer.Writer.CompleteAsync();

            return await _server.WaitAsync(_timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                await CompleteAsync();
            }

            _timeout.Dispose();
        }

        private static string Text(CallToolResult result)
        {
            return result.Content.OfType<TextContentBlock>().Single().Text;
        }
    }

    private sealed class CountingConnectionFactory : IDatabaseReadConnectionFactory
    {
        private int _openCount;

        public int OpenCount => Volatile.Read(ref _openCount);

        public Task<DbConnection> OpenAsync(
            IDatabaseReadProvider provider,
            string connectionString,
            string requestId,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _openCount);

            return DatabaseReadConnectionFactory.Instance.OpenAsync(
                provider,
                connectionString,
                requestId,
                limits,
                cancellationToken);
        }
    }
}
