using NewHeap.Platform.AI.AspNet;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The MCP exposure of an assistant host asks the application's <see cref="INhAssistantCompositionSwitch"/>
/// for the MCP channel, with the request's tenant, person and agent, instead of reading configuration.
/// </summary>
internal sealed class NhAssistantMcpCompositionGate(NhAssistantCompositionGate gate) : INhAiMcpCompositionGate
{
    public async ValueTask<bool> IsOnAsync(
        NhAiCompositionFeature feature,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await gate.AskSwitchAsync(
            new NhAssistantCompositionSwitchContext(feature, NhAssistantCompositionChannel.Mcp)
            {
                TenantId = context.TenantId,
                AgentId = context.AgentId,
                PersonActorId = context.ActorId
            },
            cancellationToken);
    }
}
