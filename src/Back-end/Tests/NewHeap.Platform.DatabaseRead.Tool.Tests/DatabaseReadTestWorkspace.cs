using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

internal sealed class DatabaseReadTestWorkspace : IDisposable
{
    private const string ConnectionStringName = "NewHeapDiagnosticsReadOnly";

    private readonly string _root;

    public DatabaseReadTestWorkspace(
        string provider,
        string connectionString,
        int maximumRows = 20,
        int profileCount = 1)
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "NewHeap.Platform.DatabaseRead.Tool.Tests",
            Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(_root, "src", "Example.Api");
        var secretsPath = Path.Combine(_root, "secrets");
        ProfileCatalogPath = Path.Combine(_root, ".newheap", "database-read.json");

        var profiles = Enumerable.Range(1, profileCount)
            .ToDictionary(
                index => index == 1 ? "test" : $"test-{index}",
                _ => (object)new
                {
                    provider,
                    configurationPath = "src/Example.Api",
                    environment = "Development",
                    connectionStringName = ConnectionStringName,
                    maximumRows,
                    maximumTimeoutSeconds = 15,
                    maximumLockTimeoutMilliseconds = 2_000,
                    maximumOutputBytes = 65_536,
                    maximumCellBytes = 4_096,
                    maximumSqlBytes = 8_192
                });
        WriteJson(
            ProfileCatalogPath,
            new
            {
                schemaVersion = 1,
                profiles
            });
        WriteJson(
            Path.Combine(configurationPath, "appsettings.json"),
            new
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
        WriteJson(
            Path.Combine(secretsPath, "secrets.json"),
            new
            {
                ConnectionStrings = new Dictionary<string, string>
                {
                    [ConnectionStringName] = connectionString
                }
            });
    }

    public string ProfileCatalogPath { get; }

    public string WriteRequestFile(MemoryStream request, string fileName = "request.json")
    {
        var path = Path.Combine(_root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, request.ToArray());

        return path;
    }

    public static MemoryStream Request(
        string sql,
        object[]? parameters = null,
        int maximumRows = 10,
        int timeoutSeconds = 10,
        string? profile = "test")
    {
        var json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                profile,
                sql,
                parameters = parameters ?? [],
                limits = new
                {
                    maximumRows,
                    timeoutSeconds
                },
                reason = "Automated database diagnostics test"
            });

        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    public static MemoryStream SchemaRequest(
        string operation,
        string? schemaName = null,
        string? objectName = null,
        string? searchTerm = null)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                profile = "test",
                schema = new
                {
                    operation,
                    schemaName,
                    objectName,
                    searchTerm
                },
                limits = new
                {
                    maximumRows = 10,
                    timeoutSeconds = 10
                },
                reason = "Automated database schema diagnostics test"
            });

        return new MemoryStream(Encoding.UTF8.GetBytes(json));
    }

    public static JsonDocument ParseOutput(MemoryStream output)
    {
        output.Position = 0;
        return JsonDocument.Parse(output);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }
}
