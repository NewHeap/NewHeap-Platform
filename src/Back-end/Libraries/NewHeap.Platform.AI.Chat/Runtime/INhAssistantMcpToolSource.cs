using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Governed functions imported from the MCP servers assigned to an agent for one turn.
/// Disposing releases connections that belong to the turn only.
/// </summary>
internal sealed class NhAssistantMcpToolSet(
    IReadOnlyList<AIFunction> functions,
    Func<ValueTask>? dispose = null) : IAsyncDisposable
{
    public static NhAssistantMcpToolSet Empty { get; } = new([]);

    public IReadOnlyList<AIFunction> Functions { get; } = functions;

    public IReadOnlyList<NhAiToolDescriptor> Descriptors { get; } = functions
        .OfType<INhAiGovernedAIFunction>()
        .Select(function => function.Descriptor)
        .ToArray();

    public ValueTask DisposeAsync()
    {
        return dispose?.Invoke() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Supplies the enabled tools of the MCP servers assigned to an agent. An unreachable server is
/// skipped so the turn continues with the remaining tools.
/// </summary>
internal interface INhAssistantMcpToolSource
{
    Task<NhAssistantMcpToolSet> GetToolsAsync(
        NhAssistantAgent agent,
        CancellationToken cancellationToken);
}

internal sealed class NhAssistantNoMcpToolSource : INhAssistantMcpToolSource
{
    public Task<NhAssistantMcpToolSet> GetToolsAsync(NhAssistantAgent agent, CancellationToken cancellationToken)
    {
        return Task.FromResult(NhAssistantMcpToolSet.Empty);
    }
}
