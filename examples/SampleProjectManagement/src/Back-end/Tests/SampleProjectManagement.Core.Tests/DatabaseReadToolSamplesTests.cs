using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NewHeap.Platform.DatabaseRead;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-218: consumers can validate parameterized data and typed schema diagnostic
/// requests against a checked-in profile without opening the database or handling secrets.
/// </summary>
public sealed class DatabaseReadToolSamplesTests
{
    [Fact]
    public void CheckedInCatalogProvidesOneAuthoritativeDiagnosticRoute()
    {
        var backendRoot = FindBackendRoot();
        var profileCatalogPath = Path.Combine(backendRoot, ".newheap", "database-read.json");
        using var catalog = JsonDocument.Parse(File.ReadAllText(profileCatalogPath));
        var profiles = catalog.RootElement.GetProperty("profiles");

        var profile = Assert.Single(profiles.EnumerateObject());

        Assert.Equal("sample-development", profile.Name);
        Assert.Equal("postgresql", profile.Value.GetProperty("provider").GetString());
        Assert.Equal("Development", profile.Value.GetProperty("environment").GetString());
        Assert.Equal(
            "NewHeapDiagnosticsReadOnly",
            profile.Value.GetProperty("connectionStringName").GetString());
        Assert.False(profile.Value.TryGetProperty("connectionString", out _));
    }

    [Fact]
    public async Task CheckedInDiagnosticRequestMatchesTheDevelopmentProfile()
    {
        var backendRoot = FindBackendRoot();
        var profileCatalogPath = Path.Combine(backendRoot, ".newheap", "database-read.json");
        var requestPath = Path.Combine(
            backendRoot,
            "Tooling",
            "DatabaseRead",
            "requests",
            "project-by-id.json");
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", profileCatalogPath, "--request-file", requestPath],
            input,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        output.Position = 0;
        using var response = await JsonDocument.ParseAsync(
            output,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("validate", response.RootElement.GetProperty("operation").GetString());
        Assert.Equal(
            "sample-development",
            response.RootElement.GetProperty("target").GetProperty("profile").GetString());
        Assert.Equal(
            "postgresql",
            response.RootElement.GetProperty("target").GetProperty("provider").GetString());
        Assert.False(
            response.RootElement.GetProperty("target").TryGetProperty("readOnlyVerified", out _));
        Assert.Equal(
            10,
            response.RootElement.GetProperty("validation").GetProperty("effectiveLimits")
                .GetProperty("maximumRows").GetInt32());
    }

    [Fact]
    public async Task CheckedInSchemaRequestMatchesTheDevelopmentProfile()
    {
        var backendRoot = FindBackendRoot();
        var profileCatalogPath = Path.Combine(backendRoot, ".newheap", "database-read.json");
        var requestPath = Path.Combine(
            backendRoot,
            "Tooling",
            "DatabaseRead",
            "requests",
            "project-schema.json");
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", profileCatalogPath, "--request-file", requestPath],
            input,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        output.Position = 0;
        using var response = await JsonDocument.ParseAsync(
            output,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("validate", response.RootElement.GetProperty("operation").GetString());
        Assert.Equal(
            "postgresql",
            response.RootElement.GetProperty("target").GetProperty("provider").GetString());
        Assert.Equal(
            100,
            response.RootElement.GetProperty("validation").GetProperty("effectiveLimits")
                .GetProperty("maximumRows").GetInt32());
    }

    [Fact]
    public async Task CheckedInIndexesRequestMatchesTheDevelopmentProfile()
    {
        var backendRoot = FindBackendRoot();
        var profileCatalogPath = Path.Combine(backendRoot, ".newheap", "database-read.json");
        var requestPath = Path.Combine(
            backendRoot,
            "Tooling",
            "DatabaseRead",
            "requests",
            "project-indexes.json");
        await using var input = File.OpenRead(requestPath);
        await using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", profileCatalogPath],
            input,
            output,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        output.Position = 0;
        using var response = await JsonDocument.ParseAsync(
            output,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("validate", response.RootElement.GetProperty("operation").GetString());
        Assert.Equal(
            "postgresql",
            response.RootElement.GetProperty("target").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task UnresolvedProductionSecretReturnsTheProcessLocalPathRemediation()
    {
        const string connectionStringName = "NewHeapDiagnosticsReadOnly";
        const string canary = "CANARY-SAMPLE-CONNECTION-CONFIGURATION-MUST-NOT-LEAK";
        var root = Path.Combine(
            Path.GetTempPath(),
            "SampleProjectManagement.DatabaseRead.Tool.Tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var configurationPath = Path.Combine(root, "src", "Sample.Api");
            var secretsPath = Path.Combine(root, "secrets");
            var profileCatalogPath = Path.Combine(root, ".newheap", "database-read.json");
            WriteJson(profileCatalogPath, new
            {
                schemaVersion = 1,
                profiles = new Dictionary<string, object>
                {
                    ["sample-development"] = new
                    {
                        provider = "postgresql",
                        configurationPath = "src/Sample.Api",
                        environment = "Development",
                        connectionStringName,
                        maximumRows = 10,
                        maximumTimeoutSeconds = 10,
                        maximumLockTimeoutMilliseconds = 2_000,
                        maximumOutputBytes = 65_536,
                        maximumCellBytes = 4_096,
                        maximumSqlBytes = 8_192
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
                    [connectionStringName] = $"${{Secrets:ConnectionStrings:{connectionStringName}}}"
                }
            });
            WriteJson(Path.Combine(configurationPath, "appsettings.Production.json"), new
            {
                NewHeap = new
                {
                    PlatformCommon = new
                    {
                        AppSecretsDirectoryPath = "/var/run/secrets/SampleProjectManagement.Api"
                    }
                }
            });
            WriteJson(Path.Combine(secretsPath, "secrets.json"), new
            {
                ConnectionStrings = new Dictionary<string, string>
                {
                    [connectionStringName] = $"DefinitelyNotAProviderKeyword={canary}"
                }
            });
            await using var input = new MemoryStream();
            await using var output = new MemoryStream();

            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                [
                    "schema",
                    "--profiles",
                    profileCatalogPath,
                    "--environment",
                    "Production",
                    "--search",
                    "Projects",
                    "--describe-if-single"
                ],
                input,
                output,
                TestContext.Current.CancellationToken);

            var outputText = Encoding.UTF8.GetString(output.ToArray());
            Assert.Equal(3, exitCode);
            Assert.DoesNotContain(canary, outputText, StringComparison.Ordinal);
            using var response = JsonDocument.Parse(outputText);
            Assert.Equal(
                "connection-string-unresolved",
                response.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains(
                "NewHeap__PlatformCommon__AppSecretsDirectoryPath",
                response.RootElement.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.False(response.RootElement.TryGetProperty("schema", out _));
            Assert.False(response.RootElement.TryGetProperty("result", out _));

            var toolAssemblyPath = typeof(NewHeapDatabaseReadApplication).Assembly.Location;
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(toolAssemblyPath)!
            };
            startInfo.Environment["NewHeap__PlatformCommon__AppSecretsDirectoryPath"] = secretsPath;
            startInfo.ArgumentList.Add(toolAssemblyPath);
            startInfo.ArgumentList.Add("schema");
            startInfo.ArgumentList.Add("--profiles");
            startInfo.ArgumentList.Add(profileCatalogPath);
            startInfo.ArgumentList.Add("--environment");
            startInfo.ArgumentList.Add("Production");
            startInfo.ArgumentList.Add("--search");
            startInfo.ArgumentList.Add("Projects");
            startInfo.ArgumentList.Add("--describe-if-single");

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var retryOutput = process!.StandardOutput.ReadToEndAsync(
                TestContext.Current.CancellationToken);
            var retryError = process.StandardError.ReadToEndAsync(
                TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var retryOutputText = await retryOutput;
            var retryErrorText = await retryError;

            Assert.Equal(3, process.ExitCode);
            Assert.DoesNotContain(canary, retryOutputText, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, retryErrorText, StringComparison.Ordinal);
            using var retryResponse = JsonDocument.Parse(retryOutputText);
            Assert.Equal(
                "connection-configuration-invalid",
                retryResponse.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".newheap", "database-read.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SampleProjectManagement backend database-read profile.");
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }
}
