using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI;
using NewHeap.Platform.DatabaseRead;
using Npgsql;
using SampleProjectManagement.DatabaseRead.Mcp;
using Testcontainers.PostgreSql;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-218: the consumer-owned MCP boundary reads live schema and data through
/// a dedicated PostgreSQL read-only principal and the NewHeap database tool.
/// </summary>
public sealed class DatabaseReadMcpSamplesTests
{
    [Fact]
    public async Task GovernedMcpReadsLiveSchemaAndExecutesAReadOnlyQuery()
    {
        await using var workspace = await LiveDatabaseReadWorkspace.CreateAsync(
            TestContext.Current.CancellationToken);
        await AssertDirectPromptRouteAsync(workspace);

        var context = SampleDatabaseReadMcpContext.Create(
            workspace.ProfileCatalogPath,
            "sample-live",
            "sample-test-owner",
            Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddSampleDatabaseReadMcp(context);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var mcpTools = await SampleDatabaseReadMcpServer.CreateToolsAsync(
            scope.ServiceProvider,
            context,
            TestContext.Current.CancellationToken);
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        await using var server = McpServer.Create(
            new StreamServerTransport(
                clientToServer.Reader.AsStream(),
                serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ScopeRequests = false,
                ToolCollection = [.. mcpTools]
            },
            serviceProvider: scope.ServiceProvider);
        _ = server.RunAsync(TestContext.Current.CancellationToken);
        await using var client = await McpClient.CreateAsync(
            new StreamClientTransport(
                clientToServer.Writer.AsStream(),
                serverToClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, tools.Count);
        Assert.Equal(
            SampleDatabaseReadMcpServer.ToolNames.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Contains(
            "relationships",
            tools.Single(tool => tool.Name == SampleDatabaseReadMcpServer.SchemaToolName).Description
            ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "expression",
            tools.Single(tool => tool.Name == SampleDatabaseReadMcpServer.IndexesToolName).Description
            ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "at most one",
            tools.Single(tool => tool.Name == SampleDatabaseReadMcpServer.IndexesToolName).Description
            ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "schema description",
            tools.Single(tool => tool.Name == SampleDatabaseReadMcpServer.QueryToolName).Description
            ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "otherwise continue",
            tools.Single(tool => tool.Name == SampleDatabaseReadMcpServer.QueryToolName).Description
            ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var schemaResult = await tools
            .Single(tool => tool.Name == SampleDatabaseReadMcpServer.SchemaToolName)
            .CallAsync(
                new Dictionary<string, object?>
                {
                    ["input"] = new SampleDatabaseSchemaInput(
                        "describe",
                        "public",
                        "Projects",
                        null,
                        "Confirm the live project identifiers before querying")
                },
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, schemaResult.IsError);
        var schemaData = SuccessfulData(schemaResult.StructuredContent);
        var describedObject = schemaData.GetProperty("schema").GetProperty("object");
        Assert.Equal("\"public\".\"Projects\"", describedObject.GetProperty("sqlIdentifier").GetString());
        Assert.Contains(
            describedObject.GetProperty("columns").EnumerateArray(),
            column => column.GetProperty("name").GetString() == "Name");
        var relationships = describedObject.GetProperty("relationships");
        var organizationRelationship = Assert.Single(
            relationships.GetProperty("outgoing").EnumerateArray(),
            relationship => relationship.GetProperty("name").GetString()
                            == "FK_Projects_Organizations_Organization");
        Assert.Equal(
            "\"public\".\"Projects\"",
            organizationRelationship.GetProperty("source").GetProperty("sqlIdentifier").GetString());
        Assert.Equal(
            "\"public\".\"Organizations\"",
            organizationRelationship.GetProperty("target").GetProperty("sqlIdentifier").GetString());
        Assert.True(organizationRelationship.GetProperty("isValidated").GetBoolean());
        Assert.Equal(
            [1, 2],
            organizationRelationship.GetProperty("columnPairs").EnumerateArray()
                .Select(pair => pair.GetProperty("position").GetInt32()));
        Assert.Equal(
            ["OrganizationTenantId", "OrganizationId"],
            organizationRelationship.GetProperty("columnPairs").EnumerateArray()
                .Select(pair => pair.GetProperty("sourceColumn").GetString()));
        Assert.Equal(
            ["TenantId", "Id"],
            organizationRelationship.GetProperty("columnPairs").EnumerateArray()
                .Select(pair => pair.GetProperty("targetColumn").GetString()));
        var taskRelationship = Assert.Single(
            relationships.GetProperty("incoming").EnumerateArray(),
            relationship => relationship.GetProperty("name").GetString()
                            == "FK_ProjectTasks_Projects_ProjectId");
        Assert.Equal(
            "Projects",
            taskRelationship.GetProperty("target").GetProperty("name").GetString());
        Assert.Equal(
            "ProjectTasks",
            taskRelationship.GetProperty("source").GetProperty("name").GetString());
        Assert.True(taskRelationship.GetProperty("isValidated").GetBoolean());

        var indexesResult = await tools
            .Single(tool => tool.Name == SampleDatabaseReadMcpServer.IndexesToolName)
            .CallAsync(
                new Dictionary<string, object?>
                {
                    ["input"] = new SampleDatabaseIndexesInput(
                        "public",
                        "Projects",
                        "Choose an indexed predicate and ordering before querying")
                },
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, indexesResult.IsError);
        var indexesData = SuccessfulData(indexesResult.StructuredContent);
        var indexSet = indexesData.GetProperty("schema").GetProperty("indexes");
        Assert.Equal("\"public\".\"Projects\"", indexSet.GetProperty("sqlIdentifier").GetString());
        var projectIndex = Assert.Single(
            indexSet.GetProperty("items").EnumerateArray(),
            index => index.GetProperty("name").GetString() == "IX_Projects_Name_Id");
        Assert.Equal(
            ["Name", "Id"],
            projectIndex.GetProperty("keyColumns").EnumerateArray()
                .Select(column => column.GetProperty("name").GetString()));
        Assert.Equal(
            ["ascending", "descending"],
            projectIndex.GetProperty("keyColumns").EnumerateArray()
                .Select(column => column.GetProperty("direction").GetString()));
        Assert.Equal(
            [1, 2],
            projectIndex.GetProperty("keyColumns").EnumerateArray()
                .Select(column => column.GetProperty("position").GetInt32()));
        Assert.All(
            projectIndex.GetProperty("keyColumns").EnumerateArray(),
            column => Assert.Equal("column", column.GetProperty("kind").GetString()));

        var partialIndex = Assert.Single(
            indexSet.GetProperty("items").EnumerateArray(),
            index => index.GetProperty("name").GetString() == "IX_Projects_Name_Partial");
        Assert.Contains(
            "Name",
            partialIndex.GetProperty("predicate").GetString(),
            StringComparison.Ordinal);

        var expressionIndex = Assert.Single(
            indexSet.GetProperty("items").EnumerateArray(),
            index => index.GetProperty("name").GetString() == "IX_Projects_Lower_Name_Id");
        var expressionKeys = expressionIndex.GetProperty("keyColumns").EnumerateArray().ToArray();
        Assert.Equal(2, expressionKeys.Length);
        Assert.Equal(1, expressionKeys[0].GetProperty("position").GetInt32());
        Assert.Equal("expression", expressionKeys[0].GetProperty("kind").GetString());
        Assert.Contains(
            "lower",
            expressionKeys[0].GetProperty("expression").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ascending", expressionKeys[0].GetProperty("direction").GetString());
        Assert.Equal(2, expressionKeys[1].GetProperty("position").GetInt32());
        Assert.Equal("column", expressionKeys[1].GetProperty("kind").GetString());
        Assert.Equal("Id", expressionKeys[1].GetProperty("name").GetString());
        Assert.Equal("descending", expressionKeys[1].GetProperty("direction").GetString());

        var queryResult = await tools
            .Single(tool => tool.Name == SampleDatabaseReadMcpServer.QueryToolName)
            .CallAsync(
                new Dictionary<string, object?>
                {
                    ["input"] = new SampleDatabaseQueryInput(
                        "SELECT \"Id\", \"Name\" FROM public.\"Projects\" WHERE \"Id\" = @projectId LIMIT 10",
                        [
                            new SampleDatabaseParameterInput(
                                "projectId",
                                "uuid",
                                JsonSerializer.SerializeToElement(
                                    "f12ea625-3b2f-4417-9691-832017358d83"))
                        ],
                        10,
                        "Read the seeded project through the governed MCP tool")
                },
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, queryResult.IsError);
        var queryData = SuccessfulData(queryResult.StructuredContent);
        var result = queryData.GetProperty("result");
        Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
        Assert.Equal("MCP sample project", result.GetProperty("rows")[0][1].GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());

        var excessiveInput = new SampleDatabaseQueryInput(
            "SELECT definitely_missing_column FROM public.\"Projects\" LIMIT 5000",
            null,
            5_000,
            "Prove an excessive request fails before query execution");
        var directExcessiveResult = await scope.ServiceProvider
            .GetRequiredService<ISampleDatabaseReadExecutor>()
            .QueryAsync(excessiveInput, TestContext.Current.CancellationToken);
        Assert.False(directExcessiveResult.Success);
        Assert.Contains(
            directExcessiveResult.AllErrorMessages,
            error => error.ToString().Contains("query was not executed", StringComparison.OrdinalIgnoreCase));

        var excessiveRequest = await tools
            .Single(tool => tool.Name == SampleDatabaseReadMcpServer.QueryToolName)
            .CallAsync(
                new Dictionary<string, object?>
                {
                    ["input"] = excessiveInput
                },
                cancellationToken: TestContext.Current.CancellationToken);
        AssertMcpFailure(excessiveRequest);

        var overflowingResult = await tools
            .Single(tool => tool.Name == SampleDatabaseReadMcpServer.QueryToolName)
            .CallAsync(
                new Dictionary<string, object?>
                {
                    ["input"] = new SampleDatabaseQueryInput(
                        "SELECT \"Id\", \"Name\" FROM public.\"Projects\" ORDER BY \"Id\" LIMIT 2",
                        null,
                        1,
                        "Prove a result above the requested limit fails without partial data")
                },
                cancellationToken: TestContext.Current.CancellationToken);
        AssertMcpFailure(overflowingResult);
    }

    [Fact]
    public async Task SampleBudgetAllowsSixteenCallsPerInvocation()
    {
        var manager = new SampleDatabaseReadMcpBudgetManager();
        var invocationId = Guid.NewGuid();

        for (var call = 1; call <= SampleDatabaseReadLimits.ToolCallBudget; call++)
        {
            var reservation = await manager.ReserveAsync(
                new NhAiBudgetRequest(invocationId, "sample-live", 1, 0, 0, null),
                TestContext.Current.CancellationToken);

            Assert.True(reservation.Success);
        }

        var exhausted = await manager.ReserveAsync(
            new NhAiBudgetRequest(invocationId, "sample-live", 1, 0, 0, null),
            TestContext.Current.CancellationToken);
        Assert.False(exhausted.Success);

        var nextInvocation = await manager.ReserveAsync(
            new NhAiBudgetRequest(Guid.NewGuid(), "sample-live", 1, 0, 0, null),
            TestContext.Current.CancellationToken);
        Assert.True(nextInvocation.Success);
    }

    private static async Task AssertDirectPromptRouteAsync(LiveDatabaseReadWorkspace workspace)
    {
        await using var schemaInput = new MemoryStream();
        await using var schemaOutput = new MemoryStream();
        var schemaExitCode = await NewHeapDatabaseReadApplication.RunAsync(
            [
                "schema",
                "--profiles",
                workspace.ProfileCatalogPath,
                "--environment",
                "Production",
                "--search",
                "Projects",
                "--schema-name",
                "public",
                "--describe-if-single",
                "--maximum-rows",
                "10",
                "--timeout-seconds",
                "10"
            ],
            schemaInput,
            schemaOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, schemaExitCode);
        schemaOutput.Position = 0;
        using var schemaResponse = await JsonDocument.ParseAsync(
            schemaOutput,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "Production",
            schemaResponse.RootElement.GetProperty("target").GetProperty("environment").GetString());
        var schema = schemaResponse.RootElement.GetProperty("schema");
        Assert.Equal("search-and-describe", schema.GetProperty("operation").GetString());
        Assert.Single(schema.GetProperty("objects").EnumerateArray());
        var describedObject = schema.GetProperty("object");
        Assert.Equal("Projects", describedObject.GetProperty("name").GetString());
        Assert.Contains(
            describedObject.GetProperty("columns").EnumerateArray(),
            column => column.GetProperty("name").GetString() == "CreationDateTime");

        var queryBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            sql =
                "SELECT COUNT(*) OVER () AS \"TotalProjects\", \"Id\", \"Name\", \"CreationDateTime\" " +
                "FROM public.\"Projects\" ORDER BY \"CreationDateTime\" DESC LIMIT 1",
            parameters = Array.Empty<object>(),
            limits = new
            {
                maximumRows = 1,
                timeoutSeconds = 10
            },
            reason = "Count projects and return the project most recently added by CreationDateTime"
        });
        await using var queryInput = new MemoryStream(queryBytes);
        await using var queryOutput = new MemoryStream();
        var queryExitCode = await NewHeapDatabaseReadApplication.RunAsync(
            [
                "query",
                "--profiles",
                workspace.ProfileCatalogPath,
                "--environment",
                "Production"
            ],
            queryInput,
            queryOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, queryExitCode);
        queryOutput.Position = 0;
        using var queryResponse = await JsonDocument.ParseAsync(
            queryOutput,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            "Production",
            queryResponse.RootElement.GetProperty("target").GetProperty("environment").GetString());
        var result = queryResponse.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
        var newestProject = result.GetProperty("rows")[0];
        Assert.Equal("2", newestProject[0].GetString());
        Assert.Equal("MCP overflow sentinel", newestProject[2].GetString());
    }

    private static JsonElement SuccessfulData(JsonElement? structuredContent)
    {
        Assert.True(structuredContent.HasValue);
        var content = structuredContent.Value;
        Assert.True(content.GetProperty("success").GetBoolean());
        return content.GetProperty("data");
    }

    private static void AssertMcpFailure(CallToolResult result)
    {
        var serialized = JsonSerializer.Serialize(result);
        Assert.True(result.IsError, serialized);
        Assert.False(result.StructuredContent.HasValue);
    }

    private sealed class LiveDatabaseReadWorkspace : IAsyncDisposable
    {
        private const string ReaderName = "sample_database_reader";
        private const string ReaderPassword = "Sample-reader-password-42";
        private const string ConnectionStringName = "NewHeapDiagnosticsReadOnly";

        private readonly PostgreSqlContainer _container;
        private readonly string _root;

        private LiveDatabaseReadWorkspace(
            PostgreSqlContainer container,
            string root,
            string profileCatalogPath)
        {
            _container = container;
            _root = root;
            ProfileCatalogPath = profileCatalogPath;
        }

        public string ProfileCatalogPath { get; }

        public static async Task<LiveDatabaseReadWorkspace> CreateAsync(
            CancellationToken cancellationToken)
        {
            var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await container.StartAsync(cancellationToken);
            try
            {
                await using var admin = new NpgsqlConnection(container.GetConnectionString());
                await admin.OpenAsync(cancellationToken);
                await ExecuteAsync(
                    admin,
                    """
                    CREATE TABLE public."Organizations" (
                        "TenantId" uuid NOT NULL,
                        "Id" uuid NOT NULL,
                        "Name" text NOT NULL,
                        CONSTRAINT "PK_Organizations" PRIMARY KEY ("TenantId", "Id")
                    );
                    INSERT INTO public."Organizations" ("TenantId", "Id", "Name")
                    VALUES (
                        '7ad83719-0d1d-4f2c-9986-7ad19ff96492',
                        'fe17651d-947a-40fc-bf56-a36448f6ec6a',
                        'MCP sample organization');
                    CREATE TABLE public."Projects" (
                        "Id" uuid PRIMARY KEY,
                        "OrganizationTenantId" uuid NOT NULL,
                        "OrganizationId" uuid NOT NULL,
                        "Name" text NOT NULL,
                        "CreationDateTime" timestamp with time zone NOT NULL,
                        CONSTRAINT "FK_Projects_Organizations_Organization"
                            FOREIGN KEY ("OrganizationTenantId", "OrganizationId")
                            REFERENCES public."Organizations" ("TenantId", "Id")
                    );
                    INSERT INTO public."Projects" (
                        "Id",
                        "OrganizationTenantId",
                        "OrganizationId",
                        "Name",
                        "CreationDateTime")
                    VALUES
                        (
                            'f12ea625-3b2f-4417-9691-832017358d83',
                            '7ad83719-0d1d-4f2c-9986-7ad19ff96492',
                            'fe17651d-947a-40fc-bf56-a36448f6ec6a',
                            'MCP sample project',
                            '2026-01-01T10:00:00Z'),
                        (
                            '48c9480a-f86c-4436-ae21-84a743cc36aa',
                            '7ad83719-0d1d-4f2c-9986-7ad19ff96492',
                            'fe17651d-947a-40fc-bf56-a36448f6ec6a',
                            'MCP overflow sentinel',
                            '2026-02-01T10:00:00Z');
                    CREATE TABLE public."ProjectTasks" (
                        "Id" uuid PRIMARY KEY,
                        "ProjectId" uuid NOT NULL,
                        "Title" text NOT NULL,
                        CONSTRAINT "FK_ProjectTasks_Projects_ProjectId"
                            FOREIGN KEY ("ProjectId")
                            REFERENCES public."Projects" ("Id")
                    );
                    CREATE INDEX "IX_Projects_Name_Id"
                        ON public."Projects" ("Name" ASC, "Id" DESC);
                    CREATE INDEX "IX_Projects_Name_Partial"
                        ON public."Projects" ("Name" ASC)
                        WHERE "Name" IS NOT NULL;
                    CREATE INDEX "IX_Projects_Lower_Name_Id"
                        ON public."Projects" ((lower("Name")) ASC, "Id" DESC);
                    CREATE ROLE sample_database_reader LOGIN PASSWORD 'Sample-reader-password-42';
                    GRANT CONNECT ON DATABASE postgres TO sample_database_reader;
                    GRANT USAGE ON SCHEMA public TO sample_database_reader;
                    GRANT SELECT ON TABLE
                        public."Organizations",
                        public."Projects",
                        public."ProjectTasks"
                        TO sample_database_reader;
                    """,
                    cancellationToken);

                var connectionBuilder = new NpgsqlConnectionStringBuilder(
                    container.GetConnectionString())
                {
                    Username = ReaderName,
                    Password = ReaderPassword,
                    Pooling = false
                };
                var root = Path.Combine(
                    Path.GetTempPath(),
                    "SampleProjectManagement.DatabaseRead.Mcp.Tests",
                    Guid.NewGuid().ToString("N"));
                var configurationPath = Path.Combine(
                    root,
                    "Applications",
                    "SampleProjectManagement.Api");
                var secretsPath = Path.Combine(root, "secrets");
                var profileCatalogPath = Path.Combine(root, ".newheap", "database-read.json");
                WriteJson(profileCatalogPath, new
                {
                    schemaVersion = 1,
                    profiles = new Dictionary<string, object>
                    {
                        ["sample-live"] = new
                        {
                            provider = "postgresql",
                            configurationPath = "Applications/SampleProjectManagement.Api",
                            environment = "Development",
                            connectionStringName = ConnectionStringName,
                            maximumRows = SampleDatabaseReadLimits.MaximumRows,
                            maximumTimeoutSeconds = SampleDatabaseReadLimits.TimeoutSeconds,
                            maximumLockTimeoutMilliseconds = 5_000,
                            maximumOutputBytes = SampleDatabaseReadLimits.MaximumOutputBytes,
                            maximumCellBytes = 65_536,
                            maximumSqlBytes = SampleDatabaseReadLimits.MaximumSqlBytes
                        }
                    }
                });
                WriteJson(Path.Combine(configurationPath, "appsettings.json"), new
                {
                    NewHeap = new
                    {
                        PlatformCommon = new
                        {
                            AppSecretsDirectoryPath = secretsPath
                        }
                    },
                    ConnectionStrings = new Dictionary<string, string>
                    {
                        [ConnectionStringName] = $"${{Secrets:ConnectionStrings:{ConnectionStringName}}}"
                    }
                });
                WriteJson(Path.Combine(secretsPath, "secrets.json"), new
                {
                    ConnectionStrings = new Dictionary<string, string>
                    {
                        [ConnectionStringName] = connectionBuilder.ConnectionString
                    }
                });
                return new LiveDatabaseReadWorkspace(container, root, profileCatalogPath);
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static async Task ExecuteAsync(
            NpgsqlConnection connection,
            string sql,
            CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
    }
}
