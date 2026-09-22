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

        return CreateGoverned(
            descriptor,
            (input, cancellationToken) => InvokeAsync(descriptor, action, services, input, cancellationToken),
            services);
    }

    /// <summary>
    /// Wraps a JSON-input handler as a governed function that publishes the descriptor's input
    /// schema inside the shared <c>input</c> envelope.
    /// </summary>
    internal static AIFunction CreateGoverned<T>(
        NhAiToolDescriptor descriptor,
        Func<JsonElement, CancellationToken, Task<TaskResult<T>>> handler,
        IServiceProvider services)
    {
        var inner = AIFunctionFactory.Create(handler, new AIFunctionFactoryOptions
        {
            Name = descriptor.ExportName,
            Description = descriptor.Description
        });
        return NhAiGovernedAIFunction.Create(
            descriptor,
            new NhAiBridgeSchemaFunction(inner, CreateEnvelopeSchema(descriptor.InputSchemaJson)),
            services);
    }

    /// <summary>
    /// Runs one bridge descriptor through the shared invoker and the self-HTTP executor. The
    /// optional <paramref name="validate"/> runs inside the governed invocation, before the HTTP call.
    /// The optional <paramref name="shaper"/> shapes a successful result for the gateway.
    /// </summary>
    internal static async Task<TaskResult<NhAiBridgeResponse>> InvokeAsync(
        NhAiToolDescriptor descriptor,
        NhAiBridgeActionInfo action,
        IServiceProvider services,
        JsonElement input,
        CancellationToken cancellationToken,
        Func<TaskResult<NhAiBridgeResponse>?>? validate = null,
        NhAiBridgeResultShaper? shaper = null)
    {
        var invoker = services.GetRequiredService<INhAiToolInvoker>();
        var executor = services.GetRequiredService<INhAiMvcBridgeExecutor>();
        return await invoker.InvokeAsync(
            descriptor,
            input,
            async (context, invocationCancellationToken) =>
            {
                if (validate?.Invoke() is { } rejected)
                {
                    return rejected;
                }

                try
                {
                    if (shaper is null)
                    {
                        return await executor.ExecuteAsync(
                            action,
                            descriptor,
                            input,
                            context,
                            invocationCancellationToken);
                    }
                    return await ExecuteShapedAsync(
                        executor,
                        action,
                        descriptor,
                        input,
                        context,
                        shaper,
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

    /// <summary>
    /// Uses the shaping seam of the built-in executor. A replaced executor keeps its own request
    /// and read behavior; its complete JSON result is shaped afterwards and a count query
    /// requests one item on the first page.
    /// </summary>
    private static async Task<TaskResult<NhAiBridgeResponse>> ExecuteShapedAsync(
        INhAiMvcBridgeExecutor executor,
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        JsonElement input,
        NhAiInvocationContext context,
        NhAiBridgeResultShaper shaper,
        CancellationToken cancellationToken)
    {
        if (executor is INhAiMvcBridgeShapingExecutor shapingExecutor)
        {
            return await shapingExecutor.ExecuteShapedAsync(
                action,
                descriptor,
                input,
                context,
                shaper,
                cancellationToken);
        }

        var executorInput = shaper.CountOnly ? NhAiBridgeCountInput.SinglePage(input) : input;
        var result = await executor.ExecuteAsync(action, descriptor, executorInput, context, cancellationToken);
        if (!result.Success || result.Data is null)
        {
            return result;
        }
        return shaper.Shape(result.Data, descriptor.MaxResultBytes);
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
