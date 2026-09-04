using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI;
using NewHeap.Platform.Common.Models;

var services = new ServiceCollection();
services.AddSingleton<AotSmokeTools>();
services.AddSingleton<INhAiToolInvoker, AotSmokeInvoker>();
services.AddSingleton<INhAiToolCatalog, AotSmokeToolsNhAiCatalog>();
using var provider = services.BuildServiceProvider();
var catalog = provider.GetRequiredService<INhAiToolCatalog>();
var functions = catalog.CreateFunctions(provider);
if (catalog.Descriptors.Count != 1
    || catalog.Manifest.Tools.Count != 1
    || functions.Count != 1)
{
    throw new InvalidOperationException("Generated AI tool registration did not survive trimming.");
}
var output = await functions[0].InvokeAsync(new AIFunctionArguments
{
    ["input"] = new AotSmokeReadInput("value")
});
if (output is not JsonElement json
    || !json.GetProperty("success").GetBoolean()
    || json.GetProperty("data").GetString() != "value:aot-smoke")
{
    throw new InvalidOperationException("Generated AI tool invocation did not survive trimming.");
}
Console.WriteLine(catalog.Manifest.SchemaHash);


public sealed record AotSmokeReadInput(string Value);

[NhAiToolSet(
    "aot-smoke",
    JsonSerializerContextType = typeof(AotSmokeJsonContext))]
public sealed class AotSmokeTools
{
    [NhAiTool("read", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Read deterministic AOT smoke data.")]
    public Task<TaskResult<string>> ReadAsync(
        AotSmokeReadInput input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TaskResult<string>.Succeeded(
            $"{input.Value}:{context.ActorId}"));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AotSmokeReadInput))]
[JsonSerializable(typeof(TaskResult<string>))]
internal partial class AotSmokeJsonContext : JsonSerializerContext
{
}

public sealed class AotSmokeInvoker : INhAiToolInvoker
{
    private static readonly NhAiInvocationContext Context = new(
        "aot-smoke",
        "aot-smoke",
        new Dictionary<string, string>());

    public Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        CancellationToken cancellationToken = default)
    {
        return invocation(Context, cancellationToken);
    }

    public Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        object arguments,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        CancellationToken cancellationToken = default)
    {
        return invocation(Context, cancellationToken);
    }
}
