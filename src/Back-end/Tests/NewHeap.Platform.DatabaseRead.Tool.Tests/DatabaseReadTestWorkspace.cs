using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

internal sealed class DatabaseReadTestWorkspace : IDisposable
{
    private const string ConnectionStringName = "NewHeapDiagnosticsReadOnly";

    private readonly string _root;

    public DatabaseReadTestWorkspace(string provider, string connectionString)
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "NewHeap.Platform.DatabaseRead.Tool.Tests",
            Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(_root, "src", "Example.Api");
        var secretsPath = Path.Combine(_root, "secrets");
        ProfileCatalogPath = Path.Combine(_root, ".newheap", "database-read.json");

        WriteJson(
            ProfileCatalogPath,
            new
            {
                schemaVersion = 1,
                profiles = new Dictionary<string, object>
                {
                    ["test"] = new
                    {
                        provider,
                        configurationPath = "src/Example.Api",
                        environment = "Development",
                        connectionStringName = ConnectionStringName,
                        maximumRows = 20,
                        maximumTimeoutSeconds = 15,
                        maximumLockTimeoutMilliseconds = 2_000,
                        maximumOutputBytes = 65_536,
                        maximumCellBytes = 4_096,
                        maximumSqlBytes = 8_192
                    }
                }
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

    public static MemoryStream Request(string sql, object[]? parameters = null)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                profile = "test",
                sql,
                parameters = parameters ?? [],
                limits = new
                {
                    maximumRows = 10,
                    timeoutSeconds = 10
                },
                reason = "Automated database diagnostics test"
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
