using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NewHeap.Platform.DatabaseRead;
using SampleProjectManagement.DatabaseRead.Mcp;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-237: consumers can validate parameterized data and typed schema diagnostic
/// requests against a checked-in profile without opening the database or handling secrets.
/// </summary>
public sealed class DatabaseReadToolSamplesTests
{
    [Theory]
    [InlineData("function-select", true)]
    [InlineData("database-role-db_owner", true)]
    [InlineData("predefined-role-membership", true)]
    [InlineData("catalog-function-execute", true)]
    [InlineData("server-permission-view-server-state", true)]
    [InlineData("object-permission-not-allowlisted", true)]
    [InlineData("inline-function-not-schema-bound", true)]
    [InlineData("inline-function-definition-not-visible", true)]
    [InlineData("inline-function-dependency-not-selectable", true)]
    [InlineData("global-privilege", true)]
    [InlineData("dynamic-privilege", true)]
    [InlineData("system-schema-select", true)]
    [InlineData("definer-view", true)]
    [InlineData("grant-not-recognized", true)]
    [InlineData("private-canary", false)]
    [InlineData(null, false)]
    public void McpPreservesOnlyKnownVerificationChecks(string? check, bool expected)
    {
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            error = new { code = "read-only-principal-not-verified", verificationCheck = check,
                message = "Password=private-canary" }
        }));
        var result = NewHeapSampleDatabaseReadExecutor.SafeFailure(response.RootElement);
        Assert.False(result.Success);
        var error = Assert.Single(result.GetResultItems());
        Assert.Equal("read-only-principal-not-verified", error.Name);
        var message = Assert.Single(error.ErrorMessages).ToString();
        Assert.DoesNotContain("private-canary", message);
        Assert.Equal(expected, message.Contains("Rejected verification check:", StringComparison.Ordinal));
        if (expected)
        {
            Assert.Contains(check!, message);
        }
    }

    [Fact]
    public void McpMapsAnUntrustedCertificateToTheTrustRemedy()
    {
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "database-query-failed",
                message = "The remote certificate CN=private-canary was rejected.",
                classification = "tls-certificate-untrusted",
                provider = "postgresql",
                transient = false,
                stage = "connection-open",
                retryHint = "certificate-authority-trust-required"
            }
        }));

        var result = NewHeapSampleDatabaseReadExecutor.SafeFailure(response.RootElement);

        Assert.False(result.Success);
        var error = Assert.Single(result.GetResultItems());
        Assert.Equal("tls-certificate-untrusted", error.Name);
        var message = Assert.Single(error.ErrorMessages).ToString();
        Assert.Contains("Trust its certificate authority", message, StringComparison.Ordinal);
        Assert.Contains("Retry hint certificate-authority-trust-required.", message, StringComparison.Ordinal);
        Assert.Contains("Stage connection-open.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-canary", message);
    }

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

    /// <summary>
    /// SPM-367: a MySQL or MariaDB failure keeps its flavor and error number, and a profile that
    /// names the other flavor or a server older than the tool supports, such as MySQL 5.7, which the
    /// tool refuses before any statement, gets a remedy instead of a retry.
    /// </summary>
    [Fact]
    public void McpKeepsTheFlavorAndErrorNumberOfAMySqlFamilyFailure()
    {
        using var version = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "provider-version-not-supported",
                message = "The tool reads the privileges of MySQL 8.0 or later."
            }
        }));
        using var timeout = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "database-query-failed",
                message = "Query execution was interrupted (max_statement_time exceeded) for private-canary",
                classification = "statement-timeout",
                provider = "mariadb",
                providerCode = "1969",
                transient = false,
                stage = "query-execution"
            }
        }));
        using var mismatch = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "provider-flavor-mismatch",
                message = "The database server is not MySQL, which the profile names as its provider."
            }
        }));

        var timeoutMessage = Assert.Single(Assert.Single(
            NewHeapSampleDatabaseReadExecutor.SafeFailure(timeout.RootElement).GetResultItems()).ErrorMessages).ToString();
        var mismatchError = Assert.Single(
            NewHeapSampleDatabaseReadExecutor.SafeFailure(mismatch.RootElement).GetResultItems());

        Assert.Contains("Provider mariadb code 1969.", timeoutMessage, StringComparison.Ordinal);
        Assert.Contains("Narrow the query", timeoutMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-canary", timeoutMessage);
        Assert.Equal("provider-flavor-mismatch", mismatchError.Name);
        Assert.Contains("Correct the profile's provider", Assert.Single(mismatchError.ErrorMessages).ToString(), StringComparison.Ordinal);
        Assert.Contains("mysql-compatible", Assert.Single(mismatchError.ErrorMessages).ToString(), StringComparison.Ordinal);

        var versionError = Assert.Single(
            NewHeapSampleDatabaseReadExecutor.SafeFailure(version.RootElement).GetResultItems());
        Assert.Equal("provider-version-not-supported", versionError.Name);
        Assert.Contains(
            "MySQL 8.0 or MariaDB 10.11",
            Assert.Single(versionError.ErrorMessages).ToString(),
            StringComparison.Ordinal);
        Assert.Contains("retrying does not help", Assert.Single(versionError.ErrorMessages).ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// SPM-367 and SPM-368: a consumer catalog can name a MySQL or MariaDB profile, one
    /// <c>mysql-compatible</c> profile for both flavors, and a SQL Server profile can opt in to
    /// inline table-valued functions; the tool validates their requests without a connection and
    /// refuses the SQL Server opt-in on another provider. The sample's own database is PostgreSQL,
    /// so the platform's provider tests prove these profiles on real MySQL 8.4, MariaDB 11.4 and
    /// SQL Server, and the refusal of MySQL 5.7.
    /// </summary>
    [Theory]
    [InlineData("mysql", null, "SELECT id FROM projects WHERE id = @id LIMIT 10", 0)]
    [InlineData("mariadb", null, "SELECT id FROM projects WHERE id = @id LIMIT 10", 0)]
    [InlineData("mysql-compatible", null, "SELECT id FROM projects WHERE id = @id LIMIT 10", 0)]
    [InlineData("sql-server", "inline-table-valued-select", "SELECT TOP (10) Id FROM dbo.ReadProject(@id)", 0)]
    [InlineData("mariadb", "inline-table-valued-select", "SELECT id FROM projects WHERE id = @id LIMIT 10", 3)]
    [InlineData("mysql", null, "SELECT SLEEP(1) FROM projects WHERE id = @id", 4)]
    [InlineData("mysql-compatible", null, "SELECT SLEEP(1) FROM projects WHERE id = @id", 4)]
    public async Task ConsumerProfilesForMoreProvidersValidateLocally(
        string provider,
        string? sqlServerFunctionPolicy,
        string sql,
        int expectedExitCode)
    {
        var root = Path.Combine(Path.GetTempPath(), "SampleProjectManagement.DatabaseRead", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".newheap"));
            Directory.CreateDirectory(Path.Combine(root, "Applications", "Example.Api"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "Applications", "Example.Api", "appsettings.json"),
                "{}",
                TestContext.Current.CancellationToken);
            var catalogPath = Path.Combine(root, ".newheap", "database-read.json");
            await File.WriteAllTextAsync(
                catalogPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    profiles = new Dictionary<string, object>
                    {
                        ["diagnostics"] = new
                        {
                            provider,
                            configurationPath = "Applications/Example.Api",
                            environment = "Development",
                            connectionStringName = "NewHeapDiagnosticsReadOnly",
                            maximumRows = 100,
                            sqlServerFunctionPolicy
                        }
                    }
                }),
                TestContext.Current.CancellationToken);
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sql,
                parameters = new[] { new { name = "id", type = "uuid", value = "9894826e-92bd-4483-b842-74979cd399ee" } },
                limits = new { maximumRows = 10, timeoutSeconds = 15 },
                reason = "Validate a consumer profile of another provider"
            })));
            await using var output = new MemoryStream();

            var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
                ["validate", "--profiles", catalogPath],
                input,
                output,
                TestContext.Current.CancellationToken);

            Assert.Equal(expectedExitCode, exitCode);
            output.Position = 0;
            using var response = await JsonDocument.ParseAsync(
                output,
                cancellationToken: TestContext.Current.CancellationToken);
            if (expectedExitCode == 0)
            {
                Assert.Equal(provider, response.RootElement.GetProperty("target").GetProperty("provider").GetString());
            }
            else
            {
                Assert.Equal(
                    expectedExitCode == 3 ? "invalid-profile-value" : "function-not-allowed",
                    response.RootElement.GetProperty("error").GetProperty("code").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
            ["outbound-network"],
            response.RootElement.GetProperty("requiredCapabilities")
                .EnumerateArray()
                .Select(capability => capability.GetString()));
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
            // This assembly was copied into the test host's output. Resolve its
            // dependencies with that host, whose ASP.NET framework supplies shared
            // Microsoft.Extensions assemblies that are absent from this directory.
            var testAssemblyPath = typeof(DatabaseReadToolSamplesTests).Assembly.Location;
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(Path.ChangeExtension(testAssemblyPath, ".runtimeconfig.json"));
            startInfo.ArgumentList.Add("--depsfile");
            startInfo.ArgumentList.Add(Path.ChangeExtension(testAssemblyPath, ".deps.json"));
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
