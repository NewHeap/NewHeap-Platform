using System.Text.Json;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class CommonDependencyBoundaryTests
{
    [Fact]
    public void CommonAssemblyDoesNotReferenceAspNet()
    {
        Assert.DoesNotContain(typeof(TaskResult).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumerRuntimeDoesNotRequireAspNetOrHangfireStorage()
    {
        var assemblyName = typeof(CommonDependencyBoundaryTests).Assembly.GetName().Name;
        using var runtime = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.runtimeconfig.json")));
        var options = runtime.RootElement.GetProperty("runtimeOptions");
        var frameworks = options.TryGetProperty("frameworks", out var multiple)
            ? multiple.EnumerateArray().ToArray()
            : [options.GetProperty("framework")];

        Assert.DoesNotContain(frameworks,
            framework => framework.GetProperty("name").GetString() == "Microsoft.AspNetCore.App");

        using var dependencies = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.deps.json")));
        Assert.DoesNotContain(dependencies.RootElement.GetProperty("libraries").EnumerateObject(),
            library => library.Name.StartsWith("Hangfire.AspNetCore/", StringComparison.Ordinal)
                || library.Name.StartsWith("Hangfire.SqlServer/", StringComparison.Ordinal)
                || library.Name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
    }
}
