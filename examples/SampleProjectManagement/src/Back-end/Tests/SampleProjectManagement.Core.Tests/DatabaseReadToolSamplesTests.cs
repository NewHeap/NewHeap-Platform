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
        Assert.Equal(
            100,
            response.RootElement.GetProperty("validation").GetProperty("effectiveLimits")
                .GetProperty("maximumRows").GetInt32());
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
}
