using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
        Assert.Equal(2, tools.Count);
        Assert.Equal(
            SampleDatabaseReadMcpServer.ToolNames.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

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
                        "Read the seeded project through the governed MCP tool")
                },
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, queryResult.IsError);
        var queryData = SuccessfulData(queryResult.StructuredContent);
        var result = queryData.GetProperty("result");
        Assert.Equal(1, result.GetProperty("rowCount").GetInt32());
        Assert.Equal("MCP sample project", result.GetProperty("rows")[0][1].GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    private static JsonElement SuccessfulData(JsonElement? structuredContent)
    {
        Assert.True(structuredContent.HasValue);
        var content = structuredContent.Value;
        Assert.True(content.GetProperty("success").GetBoolean());
        return content.GetProperty("data");
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
                    CREATE TABLE public."Projects" (
                        "Id" uuid PRIMARY KEY,
                        "Name" text NOT NULL
                    );
                    INSERT INTO public."Projects" ("Id", "Name")
                    VALUES ('f12ea625-3b2f-4417-9691-832017358d83', 'MCP sample project');
                    CREATE ROLE sample_database_reader LOGIN PASSWORD 'Sample-reader-password-42';
                    GRANT CONNECT ON DATABASE postgres TO sample_database_reader;
                    GRANT USAGE ON SCHEMA public TO sample_database_reader;
                    GRANT SELECT ON TABLE public."Projects" TO sample_database_reader;
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
