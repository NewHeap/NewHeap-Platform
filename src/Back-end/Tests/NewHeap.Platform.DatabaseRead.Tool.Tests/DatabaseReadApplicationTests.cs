using AwesomeAssertions;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

public sealed class DatabaseReadApplicationTests
{
    [Fact]
    public void DirectSchemaArgumentsCreateTheCompleteRequestWithoutJsonInput()
    {
        var options = CliOptions.Parse(
        [
            "schema",
            "--profiles",
            "catalog.json",
            "--profile",
            "commerce-development",
            "--environment",
            "Production",
            "--search",
            "customer",
            "--schema-name",
            "commerce",
            "--describe-if-single",
            "--maximum-rows",
            "25",
            "--timeout-seconds",
            "8"
        ]);

        options.Command.Should().Be(DatabaseReadCommand.Schema);
        options.ProfileName.Should().Be("commerce-development");
        options.EnvironmentName.Should().Be("Production");
        options.RequestFilePath.Should().BeNull();
        options.DirectSchema.Should().NotBeNull();

        var request = options.DirectSchema!.CreateRequest(options.ProfileName);
        request.Profile.Should().Be("commerce-development");
        request.Schema!.Operation.Should().Be("search-and-describe");
        request.Schema.SearchTerm.Should().Be("customer");
        request.Schema.SchemaName.Should().Be("commerce");
        request.Limits!.MaximumRows.Should().Be(25);
        request.Limits.TimeoutSeconds.Should().Be(8);
        request.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DirectSchemaArgumentsRejectASecondJsonInputRoute()
    {
        var parse = () => CliOptions.Parse(
        [
            "schema",
            "--search",
            "customer",
            "--request-file",
            "request.json"
        ]);

        parse.Should().Throw<DatabaseReadExpectedException>()
            .Where(exception => exception.Code == "conflicting-schema-input");
    }

    [Fact]
    public async Task RequestFileKeepsLargeJsonOutOfWindowsProcessArguments()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test");
        var parameters = Enumerable.Range(0, 128)
            .Select(index => (object)new
            {
                name = $"value{index}",
                type = "string",
                value = new string('x', 300)
            })
            .ToArray();
        using var request = DatabaseReadTestWorkspace.Request("SELECT 1", parameters);
        request.Length.Should().BeGreaterThan(32_767);
        var requestPath = workspace.WriteRequestFile(request, "large-request.json");
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
        startInfo.ArgumentList.Add(toolAssemblyPath);
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add("--profiles");
        startInfo.ArgumentList.Add(workspace.ProfileCatalogPath);
        startInfo.ArgumentList.Add("--request-file");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Sum(argument => argument.Length).Should().BeLessThan(8_192);

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var standardOutput = process!.StandardOutput.ReadToEndAsync(
            cancellation.Token);
        var standardError = process.StandardError.ReadToEndAsync(
            cancellation.Token);

        await process.WaitForExitAsync(cancellation.Token);

        var outputText = await standardOutput;
        var errorText = await standardError;
        process.ExitCode.Should().Be(0, errorText);
        using var response = JsonDocument.Parse(outputText);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("operation").GetString().Should().Be("validate");
    }

    [Fact]
    public async Task MissingRequestFileReturnsASafeStableError()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", "unused");
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        using var input = new MemoryStream();
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath, "--request-file", missingPath],
            input,
            output);

        var outputText = Encoding.UTF8.GetString(output.ToArray());
        exitCode.Should().Be(2, outputText);
        outputText.Should().NotContain(missingPath);
        using var response = JsonDocument.Parse(outputText);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("request-file-unavailable");
    }

    [Fact]
    public async Task ValidateAcceptsTypedJsonWithoutOpeningTheDatabase()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test");
        using var input = DatabaseReadTestWorkspace.Request(
            "SELECT @projectId AS \"ProjectId\"",
            [new { name = "projectId", type = "uuid", value = Guid.NewGuid().ToString() }]);
        using var output = new MemoryStream();
        var connectionFactory = new ThrowingConnectionFactory(
            new TestDatabaseException("Validation must not open a database connection."));

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output,
            connectionFactory);

        exitCode.Should().Be(0);
        connectionFactory.WasCalled.Should().BeFalse();
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("operation").GetString().Should().Be("validate");
        response.RootElement.GetProperty("target").GetProperty("profile").GetString().Should().Be("test");
        response.RootElement.GetProperty("target").GetProperty("provider").GetString().Should().Be("postgresql");
        response.RootElement.GetProperty("target").TryGetProperty("readOnlyVerified", out _).Should().BeFalse();
        response.RootElement.GetProperty("requiredCapabilities")
            .EnumerateArray()
            .Select(capability => capability.GetString())
            .Should()
            .Equal("outbound-network");
        response.RootElement.GetProperty("validation").GetProperty("effectiveLimits")
            .GetProperty("maximumRows").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task OnlyCatalogProfileIsSelectedWhenTheRequestOmitsIt()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", "unused");
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1", profile: null);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("target").GetProperty("profile").GetString()
            .Should().Be("test");
    }

    [Fact]
    public async Task ExplicitEnvironmentOverridesOnlyTheProfilesRuntimeEnvironment()
    {
        const string developmentConnectionString =
            "Host=development.example.invalid;Database=example;Username=test;Password=test";
        const string productionConnectionString =
            "Host=production.example.invalid;Database=example;Username=test;Password=test";
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            developmentConnectionString);
        workspace.WriteEnvironmentConnectionString("Production", productionConnectionString);
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1", profile: null);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            [
                "validate",
                "--profiles",
                workspace.ProfileCatalogPath,
                "--environment",
                "Production"
            ],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        var target = response.RootElement.GetProperty("target");
        target.GetProperty("profile").GetString().Should().Be("test");
        target.GetProperty("provider").GetString().Should().Be("postgresql");
        target.GetProperty("environment").GetString().Should().Be("Production");

        var profile = await DatabaseReadProfileLoader.LoadAsync(
            null,
            "Production",
            workspace.ProfileCatalogPath,
            CancellationToken.None);
        profile.ConnectionStringName.Should().Be("NewHeapDiagnosticsReadOnly");
        profile.MaximumLimits.MaximumRows.Should().Be(20);
        NewHeapConnectionStringResolver.Resolve(profile).Should().Be(productionConnectionString);
    }

    [Fact]
    public async Task UnsafeEnvironmentOverrideIsRejectedBeforeConfigurationIsLoaded()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", "unused");
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1");
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            [
                "validate",
                "--profiles",
                workspace.ProfileCatalogPath,
                "--environment",
                "../Production"
            ],
            input,
            output);

        exitCode.Should().Be(3);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("invalid-profile-value");
    }

    [Theory]
    [InlineData("sql-server", "schema")]
    [InlineData("sql-server", "query")]
    [InlineData("postgresql", "schema")]
    [InlineData("postgresql", "query")]
    public async Task InvalidResolvedConnectionConfigurationReturnsASafeStableError(
        string provider,
        string command)
    {
        const string canary = "CANARY-CONNECTION-CONFIGURATION-MUST-NOT-LEAK";
        using var workspace = new DatabaseReadTestWorkspace(
            provider,
            $"DefinitelyNotAProviderKeyword={canary}");
        using var input = command == "schema"
            ? new MemoryStream()
            : DatabaseReadTestWorkspace.Request("SELECT 1");
        using var output = new MemoryStream();
        var args = command == "schema"
            ? new[]
            {
                "schema",
                "--profiles",
                workspace.ProfileCatalogPath,
                "--search",
                "project"
            }
            : new[]
            {
                "query",
                "--profiles",
                workspace.ProfileCatalogPath
            };

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            args,
            input,
            output);

        var outputText = Encoding.UTF8.GetString(output.ToArray());
        exitCode.Should().Be(3, outputText);
        outputText.Should().NotContain(canary);
        outputText.Should().NotContain("DefinitelyNotAProviderKeyword");
        using var response = JsonDocument.Parse(outputText);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("connection-configuration-invalid");
        response.RootElement.GetProperty("error").GetProperty("message").GetString()
            .Should().Be(
                "The selected environment's resolved connection string is not valid for the configured provider.");
        response.RootElement.TryGetProperty("schema", out _).Should().BeFalse();
        response.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("query")]
    public async Task UnresolvedConnectionSecretReturnsTheProcessLocalRemediation(
        string command)
    {
        const string missingSecretName = "MISSING-PRODUCTION-SECRET-MUST-NOT-LEAK";
        using var workspace = new DatabaseReadTestWorkspace(
            "sql-server",
            $"${{Secrets:ConnectionStrings:{missingSecretName}}}");
        using var input = command == "schema"
            ? new MemoryStream()
            : DatabaseReadTestWorkspace.Request("SELECT 1");
        using var output = new MemoryStream();
        var args = command == "schema"
            ? new[]
            {
                "schema",
                "--profiles",
                workspace.ProfileCatalogPath,
                "--search",
                "project"
            }
            : new[]
            {
                "query",
                "--profiles",
                workspace.ProfileCatalogPath
            };

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            args,
            input,
            output);

        var outputText = Encoding.UTF8.GetString(output.ToArray());
        exitCode.Should().Be(3, outputText);
        outputText.Should().NotContain(missingSecretName);
        using var response = JsonDocument.Parse(outputText);
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("connection-string-unresolved");
        error.GetProperty("message").GetString().Should().Contain(
            "NewHeap__PlatformCommon__AppSecretsDirectoryPath");
        response.RootElement.TryGetProperty("schema", out _).Should().BeFalse();
        response.RootElement.TryGetProperty("result", out _).Should().BeFalse();
    }

    [Fact]
    public async Task MultipleCatalogProfilesRequireAnExplicitSelection()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "unused",
            profileCount: 2);
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1", profile: null);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(2);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("missing-profile");
    }

    [Fact]
    public async Task CliProfileSelectsFromAMultiProfileCatalogForAReusableRequest()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "unused",
            profileCount: 2);
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1", profile: null);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath, "--profile", "test-2"],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("target").GetProperty("profile").GetString()
            .Should().Be("test-2");
    }

    [Fact]
    public async Task ValidateAcceptsSchemaInspectionWithoutOpeningTheDatabase()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test");
        using var input = DatabaseReadTestWorkspace.SchemaRequest(
            "describe",
            "public",
            "diagnostic_projects");
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("operation").GetString().Should().Be("validate");
        response.RootElement.GetProperty("target").GetProperty("provider").GetString()
            .Should().Be("postgresql");
    }

    [Fact]
    public async Task ConsumerProfileCanDeliberatelyPermitOneThousandRows()
    {
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            "Host=example.invalid;Database=example;Username=test;Password=test",
            maximumRows: 1_000);
        using var input = DatabaseReadTestWorkspace.Request(
            "SELECT 1 LIMIT 1000",
            maximumRows: 1_000);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("validation").GetProperty("effectiveLimits")
            .GetProperty("maximumRows").GetInt32().Should().Be(1_000);
    }

    [Fact]
    public async Task UnknownRequestPropertiesAreRejected()
    {
        using var workspace = new DatabaseReadTestWorkspace("sql-server", "unused");
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "schemaVersion": 1,
              "profile": "test",
              "sql": "SELECT 1",
              "parameters": [],
              "reason": "Test strict JSON",
              "unexpected": true
            }
            """));
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(2);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("invalid-json");
    }

    [Theory]
    [InlineData("sql-server")]
    [InlineData("postgresql")]
    public async Task BlockedNetworkIsClassifiedSafelyWithoutOpeningARealConnection(
        string provider)
    {
        const string canary = "NEVER-RETURN-THIS-PASSWORD";
        const string unsafeServerName = "PRIVATE-DATABASE-HOST-MUST-NOT-LEAK";
        using var workspace = new DatabaseReadTestWorkspace(
            provider,
            canary);
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1");
        using var output = new MemoryStream();
        var exception = new TestDatabaseException(
            $"Could not connect to {unsafeServerName} with Password={canary}.",
            new SocketException((int)SocketError.NetworkUnreachable));
        var connectionFactory = new ThrowingConnectionFactory(exception);

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output,
            connectionFactory);

        var outputText = Encoding.UTF8.GetString(output.ToArray());
        exitCode.Should().Be(5, outputText);
        connectionFactory.WasCalled.Should().BeTrue();
        outputText.Should().NotContain(canary);
        outputText.Should().NotContain("Password");
        outputText.Should().NotContain(unsafeServerName);
        outputText.Should().NotContain(exception.Message);
        using var response = JsonDocument.Parse(outputText);
        var error = response.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("database-query-failed");
        error.GetProperty("classification").GetString().Should().Be("connection-failed");
        error.GetProperty("provider").GetString().Should().Be(provider);
        error.GetProperty("stage").GetString().Should().Be("connection-open");
        error.GetProperty("transient").GetBoolean().Should().BeTrue();
        error.GetProperty("retryHint").GetString().Should().Be("network-access-required");
        error.TryGetProperty("providerCode", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ValidateRejectsInvalidTypedParameterValues()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", "unused");
        using var input = DatabaseReadTestWorkspace.Request(
            "SELECT @projectId",
            [new { name = "projectId", type = "uuid", value = "not-a-uuid" }]);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(2);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("invalid-parameter-value");
    }

    [Fact]
    public async Task ValidateRejectsUnboundedParameterCollections()
    {
        using var workspace = new DatabaseReadTestWorkspace("postgresql", "unused");
        var parameters = Enumerable.Range(0, 129)
            .Select(index => (object)new { name = $"value{index}", type = "int32", value = index })
            .ToArray();
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1", parameters);
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(2);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("too-many-parameters");
    }

    private sealed class ThrowingConnectionFactory : IDatabaseReadConnectionFactory
    {
        private readonly DbException _exception;

        public ThrowingConnectionFactory(DbException exception)
        {
            _exception = exception;
        }

        public bool WasCalled
        {
            get; private set;
        }

        public Task<DbConnection> OpenAsync(
            IDatabaseReadProvider provider,
            string connectionString,
            string requestId,
            DatabaseReadLimits limits,
            CancellationToken cancellationToken)
        {
            WasCalled = true;

            return Task.FromException<DbConnection>(_exception);
        }
    }

    private sealed class TestDatabaseException : DbException
    {
        public TestDatabaseException(string message)
            : base(message)
        {
        }

        public TestDatabaseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
