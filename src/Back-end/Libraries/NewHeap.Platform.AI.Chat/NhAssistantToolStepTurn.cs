using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// What a composition or the script tool can know of the assistant turn it is offered in before its own
/// call: the agent and the tools a step session of that tool would offer
/// (<see cref="NhAssistantToolStepSession.Tools"/>), so the script tool can describe them to the model in
/// its description. The session opened in the call is still the authority on what a step may call.
/// </summary>
/// <param name="AgentId">The agent the turn works for.</param>
/// <param name="Tools">
/// The governed tools the agent is offered in the turn that a step may call, in a stable order: never a
/// composition, the script tool or a consultation tool.
/// </param>
public sealed record NhAssistantToolStepTurn(string AgentId, IReadOnlyList<NhAiToolDescriptor> Tools)
{
    /// <summary>
    /// The turn that runs in the current asynchronous flow as <paramref name="caller"/> sees it, or
    /// <see langword="null"/> outside an assistant turn and before the turn registered its tools.
    /// </summary>
    public static NhAssistantToolStepTurn? Current(NhAiToolDescriptor caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var turn = NhAssistantExecutionScope.CurrentTurn;
        if (turn is null || turn.OfferedTools.Count == 0)
        {
            return null;
        }

        var tools = turn.OfferedTools
            .Where(descriptor => NhAssistantToolChains.MayBeStep(descriptor, caller))
            .OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ThenBy(descriptor => descriptor.Version)
            .ToArray();
        return new NhAssistantToolStepTurn(turn.Agent.Id, tools);
    }
}
