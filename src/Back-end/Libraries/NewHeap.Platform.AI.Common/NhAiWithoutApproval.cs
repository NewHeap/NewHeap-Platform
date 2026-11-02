namespace NewHeap.Platform.AI;

/// <summary>
/// How an administrator's switch lets one agent run one tool without a person's approval (FB82).
/// </summary>
public enum NhAiWithoutApprovalMode
{
    /// <summary>Every call that needs approval becomes a proposal, as without a switch.</summary>
    Off = 0,

    /// <summary>
    /// Every call still becomes a proposal that people decide, and the platform records that the call
    /// would have run without approval, so the decisions show whether the kind of action is routine.
    /// </summary>
    Shadow = 1,

    /// <summary>
    /// The governed invoker runs the call without a proposal, with every other rule intact, and audits
    /// it as run without approval with the switch's version.
    /// </summary>
    Live = 2
}

/// <summary>
/// What an administrator's switch decides for one call: its mode and the version of the switch that
/// decided it.
/// </summary>
/// <param name="Mode">The mode of the switch for the call.</param>
/// <param name="SwitchVersion">The version of the switch; <see langword="null"/> when no switch applies.</param>
public sealed record NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode Mode, int? SwitchVersion)
{
    /// <summary>No switch applies: the call needs approval as usual.</summary>
    public static NhAiWithoutApprovalDecision Off { get; } = new(NhAiWithoutApprovalMode.Off, null);
}

/// <summary>
/// The administrator's key of running a tool without approval (FB82): decides, for one call of an
/// agent's own work that needs approval, whether a switch for the agent and the tool is in shadow or
/// live. The governed invoker asks it only for a tool that allows it in code
/// (<see cref="NhAiToolDescriptor.AllowWithoutApproval"/>), only when the default effect policy asked
/// for approval and no approval evidence exists, and only for an agent's own work: never in a person's
/// turn, a consultation, a rule turn, a shadow run or a call for another object than the run's own
/// (every scope that requires approval for effects). The assistant registers the implementation that
/// reads the switches its administration stores; without the assistant every call stays
/// <see cref="NhAiWithoutApprovalMode.Off"/>.
/// </summary>
public interface INhAiWithoutApprovalSwitches
{
    ValueTask<NhAiWithoutApprovalDecision> DecideAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The rules of the one exception to "an agent's effect waits for a person": which tool contracts may
/// allow running without approval, and the approval code the audit records for such a call.
/// </summary>
public static class NhAiWithoutApproval
{
    /// <summary>
    /// The approval code of a call that an administrator's switch let run without approval. The invoker
    /// then sets <see cref="NhAiInvocationContext.WithoutApproval"/> and writes
    /// <see cref="NhAiAuditRecord.WithoutApprovalMode"/> and <see cref="NhAiAuditRecord.WithoutApprovalSwitchVersion"/>
    /// on every audit record of the call.
    /// </summary>
    public const string ApprovalCode = "without-approval";

    /// <summary>
    /// The codes of the default effect policy's approval that a switch may lift: a tool that declares
    /// <see cref="NhAiApprovalRequirement.Required"/>, an agent that answers to itself and an effect
    /// that needs approval by default. An application's own effect policy that asks for approval with
    /// another code keeps it, as do the scopes that require approval for every effect.
    /// </summary>
    private static readonly HashSet<string> LiftableCodes = new(StringComparer.Ordinal)
    {
        "approval-required",
        "agent-accountable-approval-required",
        "effect-requires-approval"
    };

    /// <summary>
    /// Whether the tool's contract allows running without approval: it opts in, is exposed to agents,
    /// names an authorization policy, changes data reversibly (<see cref="NhAiToolEffect.IdempotentMutation"/>
    /// or <see cref="NhAiToolEffect.Mutation"/>; never a read, an external side effect or a destructive
    /// effect) and leaves its approval to the platform (never an issuer or a consumer-authoritative
    /// approval).
    /// </summary>
    public static bool IsAllowedBy(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.AllowWithoutApproval
            && descriptor.CompositionFeature == NhAiCompositionFeature.None
            && (descriptor.Exposure & NhAiToolExposure.Agent) != 0
            && descriptor.RequiresAuthorization
            && descriptor.AuthorizationPolicies.Count > 0
            && descriptor.Effect is NhAiToolEffect.IdempotentMutation or NhAiToolEffect.Mutation
            && descriptor.Approval is NhAiApprovalRequirement.PolicyControlled
                or NhAiApprovalRequirement.Required
                or NhAiApprovalRequirement.NotRequired;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a descriptor allows running without approval
    /// but its contract may not; the assistant checks every tool at startup.
    /// </summary>
    public static void Validate(NhAiToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.AllowWithoutApproval && !IsAllowedBy(descriptor))
        {
            throw new InvalidOperationException(
                $"AI tool '{descriptor.Id}' allows running without approval, which requires Agent exposure, an authorization policy, "
                + "the effect IdempotentMutation or Mutation and an approval the platform decides; it cannot be a composition, "
                + "an issuer, consumer-authoritative, an external side effect or destructive.");
        }
    }

    /// <summary>
    /// Whether an effect decision is one a switch may lift.
    /// </summary>
    internal static bool CanLift(NhAiEffectDecision decision)
    {
        return decision.Kind == NhAiEffectDecisionKind.RequireApproval && LiftableCodes.Contains(decision.Code);
    }
}

/// <summary>
/// Keeps every call at <see cref="NhAiWithoutApprovalMode.Off"/>: the default without the assistant.
/// </summary>
internal sealed class NhAiNoWithoutApprovalSwitches : INhAiWithoutApprovalSwitches
{
    public ValueTask<NhAiWithoutApprovalDecision> DecideAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        object arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NhAiWithoutApprovalDecision.Off);
    }
}
