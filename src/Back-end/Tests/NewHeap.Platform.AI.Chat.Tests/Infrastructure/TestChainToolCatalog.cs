using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// Records the step sessions the test chain opened.
/// </summary>
public sealed class TestChainRecorder
{
    public ConcurrentQueue<NhAssistantToolStepSession> Sessions { get; } = new();

    public ConcurrentQueue<NhAssistantToolStepActivity> Activities { get; } = new();

    public ConcurrentQueue<string> Refusals { get; } = new();
}

/// <summary>
/// A chain tool that runs the steps its arguments name through the step runtime, one after the other,
/// and stops when the runtime stops: <c>{"input":{"steps":[{"tool":"...","arguments":{...}}]}}</c>. It
/// returns every step with its status and code. The tool runs no code of its own; every step is a
/// governed call of another tool.
/// </summary>
public sealed class TestChainToolCatalog : INhAiToolCatalog
{
    public static readonly NhAiToolDescriptor Chain = new(
        "projects.chain",
        1,
        "Run several project tools in one call.",
        typeof(JsonElement),
        typeof(JsonElement),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local | NhAiToolExposure.Agent | NhAiToolExposure.Mcp,
        true,
        [])
    {
        ExportName = "projects_chain_v1",
        CatalogId = "project-chains",
        ContractHash = new string('4', 64),
        SchemaHash = new string('4', 64),
        Approval = NhAiApprovalRequirement.NotRequired,
        AllowAgentGrant = true,
        CompositionFeature = NhAiCompositionFeature.Compositions
    };

    public NhAiToolCatalogGovernance Governance => NhAiToolCatalogGovernance.SharedInvoker;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = [Chain];

    public NhAiToolCatalogManifest Manifest { get; } = new(
        "project-chains",
        1,
        new string('5', 64),
        [new NhAiToolManifestEntry(Chain.Id, 1, Chain.SchemaHash, Chain.ContractHash) { ExportName = Chain.ExportName }]);

    public IReadOnlyList<AIFunction> CreateFunctions(IServiceProvider services)
    {
        var recorder = services.GetRequiredService<TestChainRecorder>();
        Func<JsonElement, CancellationToken, Task<JsonElement>> run = async (input, cancellationToken) =>
        {
            var begun = await services.GetRequiredService<INhAssistantToolSteps>().BeginAsync(
                new NhAssistantToolStepRequest(Chain, NhAiToolCallOriginKind.Composition)
                {
                    MaxMutationSteps = input.TryGetProperty("maxMutationSteps", out var limit) ? limit.GetInt32() : 4
                },
                cancellationToken);
            if (!begun.Success)
            {
                var code = begun.GetResultItems().Select(item => item.Name).First(name => !string.IsNullOrEmpty(name));
                recorder.Refusals.Enqueue(code);
                return NhAiToolResult.Failed(code, null);
            }

            var session = begun.Data;
            recorder.Sessions.Enqueue(session);
            foreach (var step in input.GetProperty("steps").EnumerateArray())
            {
                await session.RunAsync(step.GetProperty("tool").GetString()!, step.GetProperty("arguments"), cancellationToken);
            }

            var result = JsonSerializer.SerializeToElement(new
            {
                success = true,
                data = new
                {
                    steps = session.Steps.Select(step => new { tool = step.ToolId, status = step.Status.ToString(), code = step.Code })
                }
            });
            recorder.Activities.Enqueue(await session.CompleteAsync(result, cancellationToken));
            return result;
        };

        return
        [
            NhAiGovernedAIFunction.Create(
                Chain,
                AIFunctionFactory.Create(run, new AIFunctionFactoryOptions { Name = Chain.ExportName, Description = Chain.Description }),
                services)
        ];
    }
}
