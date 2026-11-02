using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Governance;

/// <summary>
/// The administrator's key of running a tool without approval (FB82): the switch an administrator stored
/// for the agent and the tool, read from the database for every call so switching off applies at once.
/// It answers only for a call the model or a step makes in an assistant turn, and only for the contract
/// the switch was made for; a call that does not fit every rule a proposal of it would meet stays off,
/// so it becomes the proposal it was.
/// </summary>
internal sealed class NhAssistantWithoutApprovalSwitches(NhAssistantWithoutApprovalStore store) : INhAiWithoutApprovalSwitches
{
    public async ValueTask<NhAiWithoutApprovalDecision> DecideAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var call = NhAssistantExecutionScope.CurrentCall;
        if (call?.Interceptor is not { } interceptor
            || !string.Equals(call.Descriptor.Id, descriptor.Id, StringComparison.Ordinal)
            || call.ProposalId is not null
            || string.IsNullOrWhiteSpace(context.AgentId))
        {
            return NhAiWithoutApprovalDecision.Off;
        }

        var current = await store.FindCurrentAsync(context.AgentId, descriptor.Id, cancellationToken);
        if (current is null
            || !string.Equals(current.ContractHash, descriptor.ContractHash, StringComparison.Ordinal))
        {
            // Never switched, or switched for another contract of the tool.
            return NhAiWithoutApprovalDecision.Off;
        }

        var mode = NhAssistantWithoutApprovalModes.ToMode(current.Mode);
        if (mode == NhAiWithoutApprovalMode.Off
            || !await interceptor.FitsWithoutApprovalAsync(descriptor, context, arguments, cancellationToken))
        {
            return NhAiWithoutApprovalDecision.Off;
        }

        return new NhAiWithoutApprovalDecision(mode, current.Version);
    }
}
