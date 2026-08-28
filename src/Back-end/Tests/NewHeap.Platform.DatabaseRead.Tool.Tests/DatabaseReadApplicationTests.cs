using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace NewHeap.Platform.DatabaseRead.Tool.Tests;

public sealed class DatabaseReadApplicationTests
{
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

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["validate", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        exitCode.Should().Be(0);
        using var response = DatabaseReadTestWorkspace.ParseOutput(output);
        response.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        response.RootElement.GetProperty("operation").GetString().Should().Be("validate");
        response.RootElement.GetProperty("target").GetProperty("profile").GetString().Should().Be("test");
        response.RootElement.GetProperty("target").GetProperty("provider").GetString().Should().Be("postgresql");
        response.RootElement.GetProperty("target").TryGetProperty("readOnlyVerified", out _).Should().BeFalse();
        response.RootElement.GetProperty("validation").GetProperty("effectiveLimits")
            .GetProperty("maximumRows").GetInt32().Should().Be(10);
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

    [Fact]
    public async Task DatabaseFailuresDoNotExposeConnectionStringSecrets()
    {
        const string canary = "NEVER-RETURN-THIS-PASSWORD";
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 1,
            Database = "missing",
            Username = "diagnostic",
            Password = canary,
            Timeout = 1
        }.ConnectionString;
        using var workspace = new DatabaseReadTestWorkspace(
            "postgresql",
            connectionString);
        using var input = DatabaseReadTestWorkspace.Request("SELECT 1");
        using var output = new MemoryStream();

        var exitCode = await NewHeapDatabaseReadApplication.RunAsync(
            ["query", "--profiles", workspace.ProfileCatalogPath],
            input,
            output);

        var outputText = Encoding.UTF8.GetString(output.ToArray());
        exitCode.Should().Be(5, outputText);
        outputText.Should().NotContain(canary);
        outputText.Should().NotContain("Password");
        using var response = JsonDocument.Parse(outputText);
        response.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("database-query-failed");
        response.RootElement.GetProperty("error").GetProperty("classification").GetString()
            .Should().Be("connection-failed");
        response.RootElement.GetProperty("error").GetProperty("provider").GetString()
            .Should().Be("postgresql");
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
}
