using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Creates the governed function of one bridge descriptor. The function has the same shape as
/// a generated tool: arguments in the <c>input</c> envelope, a <c>TaskResult&lt;T&gt;</c>
/// outcome and execution through <see cref="INhAiToolInvoker"/>.
/// </summary>
internal static class NhAiMvcBridgeFunctionFactory
{
    public static AIFunction Create(
        NhAiToolDescriptor descriptor,
        NhAiBridgeActionInfo action,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(services);

        Func<JsonElement, CancellationToken, Task<TaskResult<NhAiBridgeResponse>>> handler =
            (input, cancellationToken) => InvokeAsync(descriptor, action, services, input, cancellationToken);
        var inner = AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions
        {
            Name = descriptor.ExportName,
            Description = descriptor.Description
        });
        return NhAiGovernedAIFunction.Create(
            descriptor,
            new NhAiBridgeSchemaFunction(inner, CreateEnvelopeSchema(descriptor.InputSchemaJson)));
    }

    private static async Task<TaskResult<NhAiBridgeResponse>> InvokeAsync(
        NhAiToolDescriptor descriptor,
        NhAiBridgeActionInfo action,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var executor = services.GetRequiredService<INhAiMvcBridgeExecutor>();
        return await invoker.InvokeAsync(
            descriptor,
            input,
            async (context, invocationCancellationToken) =>
            {
                try
                {
                    return await executor.ExecuteAsync(
                        action,
                        descriptor,
                        input,
                        context,
                        invocationCancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Only the invoker's execution deadline cancelled the call; the caller did not.
                    return TaskResult<NhAiBridgeResponse>.Failed(
                        NhAiBridgeFailureCodes.Timeout,
                        "The API call did not complete within the tool timeout.");
                }
            },
            cancellationToken);
    }

    private static JsonElement CreateEnvelopeSchema(string inputSchemaJson)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["input"] = JsonNode.Parse(inputSchemaJson)
            },
            ["required"] = new JsonArray("input")
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// Publishes the bridge input schema inside the <c>input</c> envelope instead of the
    /// untyped <see cref="JsonElement"/> parameter schema of the factory function.
    /// </summary>
    private sealed class NhAiBridgeSchemaFunction(AIFunction inner, JsonElement schema) : AIFunction
    {
        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override JsonElement JsonSchema => schema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            return inner.InvokeAsync(arguments, cancellationToken);
        }
    }
}
