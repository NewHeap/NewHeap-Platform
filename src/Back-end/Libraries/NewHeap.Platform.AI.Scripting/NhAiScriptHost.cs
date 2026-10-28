using System.Text.Json;

namespace NewHeap.Platform.AI.Scripting;

/// <summary>
/// The turn a script runs in, as the script engine sees it: the tools the agent is offered in that turn
/// and a way to call one of them with the turn's context. The script can call only these tools. The host
/// runs every call exactly as the same call runs on its own: the consuming application's effect,
/// approval requirement, approval policy, guards and budget apply per call, and the host returns the
/// decision it took. The script tool runs its scripts on the assistant's step runtime
/// (<c>INhAssistantToolSteps</c>), in a turn and over MCP; a test can run the engine on a host of its own.
/// </summary>
/// <remarks>
/// The engine calls <see cref="CallAsync"/> one call at a time, in the order the program made them, and
/// never concurrently; it never calls a tool that is not in <see cref="Tools"/>.
/// </remarks>
public interface INhAiScriptHost
{
    /// <summary>
    /// The tools the agent is offered in this turn, in the order the model is offered them. The script
    /// tool itself is never among them.
    /// </summary>
    IReadOnlyList<NhAiScriptToolOffer> Tools { get; }

    /// <summary>
    /// Calls one offered tool with the arguments the program gave it, in the turn's context.
    /// </summary>
    ValueTask<NhAiScriptCallOutcome> CallAsync(
        NhAiScriptToolCall call,
        CancellationToken cancellationToken);
}

/// <summary>
/// A tool a script may name: the function name the model knows it by, such as
/// <c>projects_search_v1</c>, and its governed descriptor, whose input schema is the signature the script
/// sees.
/// </summary>
public sealed record NhAiScriptToolOffer
{
    public NhAiScriptToolOffer(string name, NhAiToolDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(descriptor);
        Name = name;
        Descriptor = descriptor;
    }

    /// <summary>The function name, the property of <c>tools</c> the program calls.</summary>
    public string Name { get; }

    public NhAiToolDescriptor Descriptor { get; }

    /// <summary>Whether a call changes something: every effect other than read-only.</summary>
    public bool IsMutation => Descriptor.Effect != NhAiToolEffect.ReadOnly;
}

/// <summary>
/// One call a program makes. <see cref="Arguments"/> is the object the program passed, as a call on its
/// own would send it: the input properties at the top level. <see cref="ScriptHash"/> and
/// <see cref="Sequence"/> tell the host that the call came from a script and which one, so the guard and
/// invocation context can say so.
/// </summary>
public sealed record NhAiScriptToolCall(
    NhAiScriptToolOffer Tool,
    JsonElement Arguments,
    string ScriptHash,
    int Sequence);

/// <summary>
/// The decision a host took for one call.
/// </summary>
public enum NhAiScriptCallOutcomeKind
{
    /// <summary>
    /// The call went through governance as it would on its own and <see cref="NhAiScriptCallOutcome.Result"/>
    /// is the tool's result in the shared shape, a success or a failure. The script goes on.
    /// </summary>
    Completed = 0,

    /// <summary>
    /// The call was recorded as a proposal that waits for people (a deferred approval);
    /// <see cref="NhAiScriptCallOutcome.Result"/> is the "proposal recorded" result the model gets for the same
    /// call. The action has not happened, and the script goes on.
    /// </summary>
    Proposed = 1,

    /// <summary>
    /// The call waits for a person's approval before the turn can go on (an inline approval). The script
    /// stops at this call and returns what it did so far; the turn waits as it does for the same call.
    /// </summary>
    AwaitingApproval = 2,

    /// <summary>
    /// The turn refused the call before it ran, for example at its tool-call limit or budget. The script
    /// stops with <see cref="NhAiScriptCodes.CallRefused"/>.
    /// </summary>
    Refused = 3,

    /// <summary>
    /// A proposal guard found the work already done or no longer applicable, so nothing was proposed;
    /// <see cref="NhAiScriptCallOutcome.Result"/> is the result the model gets for the same call. The script
    /// goes on.
    /// </summary>
    NotNeeded = 4
}

/// <summary>
/// What a host returns for one call: its decision and the result the model would get for the same call
/// on its own, in the shared tool result shape (<see cref="NhAiToolResult"/>).
/// </summary>
public sealed record NhAiScriptCallOutcome(
    NhAiScriptCallOutcomeKind Kind,
    JsonElement Result)
{
    /// <summary>The proposal a <see cref="NhAiScriptCallOutcomeKind.Proposed"/> or awaiting call recorded, when the host knows it.</summary>
    public Guid? ProposalId { get; init; }

    /// <summary>The call ran through governance; <paramref name="result"/> is its result.</summary>
    public static NhAiScriptCallOutcome Completed(JsonElement result)
    {
        return new NhAiScriptCallOutcome(NhAiScriptCallOutcomeKind.Completed, result);
    }

    /// <summary>A proposal was recorded and waits for people; the script goes on.</summary>
    public static NhAiScriptCallOutcome Proposed(JsonElement result, Guid? proposalId = null)
    {
        return new NhAiScriptCallOutcome(NhAiScriptCallOutcomeKind.Proposed, result) { ProposalId = proposalId };
    }

    /// <summary>The call waits for a person's approval; the script stops here.</summary>
    public static NhAiScriptCallOutcome AwaitingApproval(JsonElement result, Guid? proposalId = null)
    {
        return new NhAiScriptCallOutcome(NhAiScriptCallOutcomeKind.AwaitingApproval, result) { ProposalId = proposalId };
    }

    /// <summary>The turn refused the call before it ran; the script stops here.</summary>
    public static NhAiScriptCallOutcome Refused(JsonElement result)
    {
        return new NhAiScriptCallOutcome(NhAiScriptCallOutcomeKind.Refused, result);
    }

    /// <summary>A guard found the work done or no longer applicable; nothing was proposed.</summary>
    public static NhAiScriptCallOutcome NotNeeded(JsonElement result)
    {
        return new NhAiScriptCallOutcome(NhAiScriptCallOutcomeKind.NotNeeded, result);
    }
}
