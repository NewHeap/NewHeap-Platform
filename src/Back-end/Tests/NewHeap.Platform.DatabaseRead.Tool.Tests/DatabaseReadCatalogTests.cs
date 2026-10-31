using AwesomeAssertions;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

/// <summary>
/// The schema operation <c>catalog</c>: every selectable object, exactly as <c>describe</c> returns
/// it, on one connection and in one read-only transaction, in one-shot mode and in a session. A
/// catalog that does not fit its limits says so.
/// </summary>
public sealed class DatabaseReadCatalogTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task CatalogDescribesEverySelectableObject(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveCatalogDescribesEverySelectableObjectAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task CatalogDescribesEverySelectableObjectOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveCatalogDescribesEverySelectableObjectAsync(database);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task CatalogThatDoesNotFitReportsItselfAsTruncated(RelationalTestProvider provider)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(provider);

        await ProveCatalogReportsTruncationAsync(database);
    }

    [Theory]
    [InlineData(DatabaseReadMySqlFlavor.MySql)]
    [InlineData(DatabaseReadMySqlFlavor.MariaDb)]
    public async Task CatalogThatDoesNotFitReportsItselfAsTruncatedOnEachFlavor(DatabaseReadMySqlFlavor flavor)
    {
        await using var database = await DatabaseReadSessionDatabase.CreateAsync(flavor);

        await ProveCatalogReportsTruncationAsync(database);
    }

    [Fact]
    public async Task ValidateAcceptsACatalogWithoutOpeningAConnection()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test");
        using var input = DatabaseReadTestWorkspace.SchemaRequest("catalog", "public");
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0, DatabaseReadTestWorkspace.OutputText(output));
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("operation").GetString().Should().Be("validate");
    }

    [Theory]
    [InlineData("objectName")]
    [InlineData("searchTerm")]
    public async Task CatalogAcceptsNoOtherFilterThanTheSchema(string property)
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test");
        var json =
            $$"""
            {
              "schemaVersion": 1,
              "schema": { "operation": "catalog", "{{property}}": "items" },
              "reason": "Automated catalog test"
            }
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["schema", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(2);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-schema-catalog");
    }

    /// <summary>
    /// Each statement of a catalog keeps its own timeout, so the request's timeout has to bound the
    /// catalog as a whole. The provider here takes ten seconds per object on a clock that the test
    /// moves.
    /// </summary>
    [Fact]
    public async Task CatalogThatRunsOutOfItsTimeoutReportsItselfAsTruncated()
    {
        var host = new StubHost();
        var limits = new DatabaseReadLimits(100, 30, 1_000, 1024 * 1024, 4_096, 8_192);

        var result = await DatabaseSchemaReader.ExecuteAsync(
            new SlowCatalogProvider(host.Clock, objectCount: 8),
            host,
            new DatabaseReadExecutionContext(),
            "unused",
            "request",
            new ResolvedDatabaseSchemaRequest(DatabaseSchemaOperation.Catalog, null, null, null),
            limits,
            CancellationToken.None);

        result.Truncated.Should().BeTrue();
        result.CatalogObjects!.Should().HaveCount(3);
    }

    private static async Task ProveCatalogDescribesEverySelectableObjectAsync(DatabaseReadSessionDatabase database)
    {
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        var connections = new CountingConnectionFactory();

        using var catalog = await RunSchemaAsync(workspace, DatabaseReadTestWorkspace.SchemaRequest("catalog"), connections);

        connections.OpenCount.Should().Be(1, "a catalog is one connection and one transaction");
        var schema = catalog.RootElement.GetProperty("schema");
        schema.GetProperty("operation").GetString().Should().Be("catalog");
        schema.GetProperty("truncated").GetBoolean().Should().BeFalse();
        schema.GetProperty("evidenceHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        var objects = schema.GetProperty("objects").EnumerateArray().ToArray();
        var names = objects
            .Select(entry => entry.GetProperty("object"))
            .Select(item => $"{item.GetProperty("schema").GetString()}.{item.GetProperty("name").GetString()}")
            .ToArray();
        names.Should().Contain($"{database.Schema}.session_items");
        names.Should().Contain($"{database.Schema}.session_owners");
        names.Should().Contain($"{database.OtherSchema}.session_totals");
        names.Should().NotContain(name => name.EndsWith(".session_restricted", StringComparison.Ordinal));

        foreach (var entry in objects)
        {
            var item = entry.GetProperty("object");
            using var describe = await RunSchemaAsync(
                workspace,
                DatabaseReadTestWorkspace.SchemaRequest(
                    "describe",
                    item.GetProperty("schema").GetString(),
                    item.GetProperty("name").GetString()));
            var described = describe.RootElement.GetProperty("schema");

            item.GetRawText().Should().Be(described.GetProperty("object").GetRawText());
            entry.GetProperty("evidenceHash").GetString().Should().Be(described.GetProperty("evidenceHash").GetString());
        }

        var items = objects
            .Single(entry => entry.GetProperty("object").GetProperty("name").GetString() == "session_items")
            .GetProperty("object");
        items.GetProperty("columns").GetArrayLength().Should().Be(3);
        items.GetProperty("indexes").EnumerateArray()
            .Select(index => index.GetProperty("name").GetString())
            .Should().Contain("ix_session_items_name");
        items.GetProperty("relationships").GetProperty("outgoing")[0].GetProperty("name").GetString()
            .Should().Be("fk_session_items_session_owners");

        using var oneSchema = await RunSchemaAsync(
            workspace,
            DatabaseReadTestWorkspace.SchemaRequest("catalog", database.OtherSchema));
        var oneSchemaObjects = oneSchema.RootElement.GetProperty("schema").GetProperty("objects");
        oneSchemaObjects.GetArrayLength().Should().Be(1);
        oneSchemaObjects[0].GetProperty("object").GetProperty("name").GetString().Should().Be("session_totals");

        await using var session = new DatabaseReadSessionClient(workspace);
        await session.ReadReadyLineAsync();
        var inSession = await session.SchemaAsync("catalog");

        inSession.ExitCode.Should().Be(0, inSession.Text);
        inSession.Response.GetProperty("schema").GetProperty("evidenceHash").GetString()
            .Should().Be(schema.GetProperty("evidenceHash").GetString());
    }

    private static async Task ProveCatalogReportsTruncationAsync(DatabaseReadSessionDatabase database)
    {
        using var workspace = new DatabaseReadTestWorkspace(database.ProviderName, database.ReaderConnectionString);
        using var everything = await RunSchemaAsync(workspace, DatabaseReadTestWorkspace.SchemaRequest("catalog"));
        var total = everything.RootElement.GetProperty("schema").GetProperty("objects").GetArrayLength();

        using var limitedByRows = await RunSchemaAsync(
            workspace,
            DatabaseReadTestWorkspace.SchemaRequest("catalog", maximumRows: 1));
        var rows = limitedByRows.RootElement.GetProperty("schema");

        rows.GetProperty("objects").GetArrayLength().Should().Be(1);
        rows.GetProperty("truncated").GetBoolean().Should().BeTrue();

        using var smallOutput = new DatabaseReadTestWorkspace(
            database.ProviderName,
            database.ReaderConnectionString,
            maximumOutputBytes: 4_096);
        using var limitedByOutput = await RunSchemaAsync(
            smallOutput,
            DatabaseReadTestWorkspace.SchemaRequest("catalog"));
        var output = limitedByOutput.RootElement.GetProperty("schema");

        output.GetProperty("truncated").GetBoolean().Should().BeTrue();
        output.GetProperty("objects").GetArrayLength().Should().BeLessThan(total);
        output.GetProperty("evidenceHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        limitedByOutput.RootElement.GetRawText().Length.Should().BeLessThanOrEqualTo(4_096);
    }

    private static async Task<JsonDocument> RunSchemaAsync(
        DatabaseReadTestWorkspace workspace,
        MemoryStream input,
        IDatabaseReadConnectionFactory? connections = null)
    {
        using (input)
        {
            using var output = new MemoryStream();
            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["schema", "--profiles", workspace.ProfileCatalogPath],
                input,
                output,
                connections ?? DatabaseReadConnectionFactory.Instance);

            exitCode.Should().Be(0, "the tool returned {0}", DatabaseReadTestWorkspace.OutputText(output));

            return DatabaseReadTestWorkspace.ParseOutput(output);
        }
    }

    private sealed class CountingConnectionFactory : IDatabaseReadConnectionFactory
    {
        public int OpenCount { get; private set; }

        public Task<DbConnection> OpenAsync(
            IDatabaseReadProvider provider,
            string connectionString,
            string requestId,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            OpenCount++;

            return DatabaseReadConnectionFactory.Instance.OpenAsync(
                provider,
                connectionString,
                requestId,
                limits,
                cancellationToken);
        }
    }

    private sealed class StubHost : IDatabaseReadRequestHost
    {
        public DatabaseReadTestClock Clock { get; } = new();

        public TimeProvider TimeProvider => Clock;

        public Task<ResolvedDatabaseReadProfile> LoadProfileAsync(
            string? profileName,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public string ResolveConnectionString(ResolvedDatabaseReadProfile profile)
        {
            throw new NotSupportedException();
        }

        public Task<DbConnection> OpenVerifiedConnectionAsync(
            IDatabaseReadProvider provider,
            DatabaseReadExecutionContext executionContext,
            string connectionString,
            string requestId,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DbConnection>(new NpgsqlConnection());
        }

        public Task RollBackAsync(
            DbConnection connection,
            DbTransaction transaction,
            DatabaseReadExecutionContext executionContext)
        {
            return Task.CompletedTask;
        }

        public void OnConnectionFailed()
        {
        }
    }

    private sealed class SlowCatalogProvider(DatabaseReadTestClock clock, int objectCount) : IDatabaseReadProvider
    {
        public string Name => "stub";

        public DbConnection CreateConnection(string connectionString, string requestId, DatabaseReadLimits limits)
        {
            throw new NotSupportedException();
        }

        public string CreateReusableConnectionString(string connectionString)
        {
            throw new NotSupportedException();
        }

        public string GetPhysicalConnectionId(DbConnection connection)
        {
            throw new NotSupportedException();
        }

        public Task ClearConnectionPoolAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void EnsureSupportedServer(DbConnection connection)
        {
        }

        public DateTimeOffset NormalizeInstant(DateTimeOffset instant)
        {
            throw new NotSupportedException();
        }

        public Task<DatabaseReadPrincipalVerification> VerifyReadOnlyPrincipalAsync(
            DbConnection connection,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DatabaseReadPrincipalVerification(true));
        }

        public Task<DbTransaction> BeginReadOnlyTransactionAsync(
            DbConnection connection,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<DbTransaction>(new StubTransaction());
        }

        public Task ConfigureReadOnlyTransactionAsync(
            DbConnection connection,
            DbTransaction transaction,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<DatabaseSchemaResultResponse> ReadSchemaAsync(
            DbConnection connection,
            DbTransaction transaction,
            ResolvedDatabaseSchemaRequest request,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            if (request.Operation == DatabaseSchemaOperation.Search)
            {
                return Task.FromResult(new DatabaseSchemaResultResponse
                {
                    Operation = "search",
                    Objects = Enumerable.Range(1, objectCount)
                        .Select(index => new DatabaseSchemaObjectSummaryResponse
                        {
                            Schema = "stub",
                            Name = $"object{index}",
                            Kind = "table",
                            SqlIdentifier = $"stub.object{index}"
                        })
                        .ToList()
                });
            }

            clock.Advance(TimeSpan.FromSeconds(10));

            return Task.FromResult(new DatabaseSchemaResultResponse
            {
                Operation = "describe",
                Object = new DatabaseSchemaObjectResponse
                {
                    Schema = request.SchemaName!,
                    Name = request.ObjectName!,
                    Kind = "table",
                    SqlIdentifier = $"{request.SchemaName}.{request.ObjectName}",
                    Columns = [],
                    Indexes = [],
                    Relationships = new DatabaseSchemaRelationshipsResponse { Outgoing = [], Incoming = [] }
                }
            });
        }

        public DatabaseReadProviderFailure ClassifyException(DbException exception, DatabaseReadExecutionStage stage)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit()
        {
        }

        public override void Rollback()
        {
        }
    }
}
