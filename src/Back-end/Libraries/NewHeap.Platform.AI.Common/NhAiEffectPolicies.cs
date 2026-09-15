namespace NewHeap.Platform.AI;

public enum NhAiEffectDecisionKind
{
    Allow = 0,
    Deny = 1,
    RequireApproval = 2,

    /// <summary>
    /// Approval is delegated to the tool itself. The invoker accepts this decision
    /// only for descriptors that declare
    /// <see cref="NhAiApprovalRequirement.ConsumerAuthoritative"/>.
    /// </summary>
    ConsumerAuthoritativeApproval = 3
}

public sealed record NhAiEffectDecision(
    NhAiEffectDecisionKind Kind,
    string Code);

public interface INhAiEffectPolicy
{
    ValueTask<NhAiEffectDecision> EvaluateAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiDefaultEffectPolicy : INhAiEffectPolicy
{
    public ValueTask<NhAiEffectDecision> EvaluateAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.Approval == NhAiApprovalRequirement.Required)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.RequireApproval,
                "approval-required"));
        }
        if (descriptor.Approval == NhAiApprovalRequirement.ConsumerAuthoritative)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.ConsumerAuthoritativeApproval,
                "consumer-authoritative-approval"));
        }
        if (descriptor.Effect == NhAiToolEffect.ReadOnly)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.Allow,
                "read-only"));
        }
        if (descriptor.Effect == NhAiToolEffect.IdempotentMutation
            && descriptor.Approval == NhAiApprovalRequirement.NotRequired)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.Allow,
                "explicit-idempotent-mutation"));
        }
        return ValueTask.FromResult(new NhAiEffectDecision(
            NhAiEffectDecisionKind.RequireApproval,
            "effect-requires-approval"));
    }
}
