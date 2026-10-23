using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Every refusal the invoker audits as <see cref="NhAiOutcomeKind.AuthorizationDenied"/> records the
/// code its caller receives as the audit's result code, so what reads the audit names the real reason.
/// </summary>
public sealed class NhAiToolRefusalCodeTests
{
    [Fact]
    public async Task A_refusal_of_the_invocation_gate_is_audited_with_the_gates_own_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var gate = new NhAiTestInvocationGate((_, _) => ValueTask.FromResult(
            TaskResult<NhAiInvocationContext>.Failed(NhAiAgentFailureCodes.NotGranted, "The agent is not allowed to use this AI tool.")));
        var invoker = new NhAiToolInvoker(gate, [audit]);

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiAgentFailureCodes.NotGranted);
    }

    [Fact]
    public async Task A_refusal_of_the_invocation_gate_without_a_code_is_audited_as_authorization_denied()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(NhAiTestInvocationGate.Denied("Denied."), [audit]);

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiToolFailureCodes.AuthorizationDenied);
    }

    [Fact]
    public async Task A_missing_capability_is_audited_as_capability_denied()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(NhAiTestInvocationGate.Authorized(Context), [audit]);

        var result = await InvokeAsync(invoker, Tool with { RequiredCapabilities = ["orders-manage"] });

        AssertRefused(result, audit, NhAiToolFailureCodes.CapabilityDenied);
    }

    [Fact]
    public async Task A_refusal_of_the_effect_policy_is_audited_as_effect_denied()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new FixedEffectPolicy(NhAiEffectDecisionKind.Deny),
            new MissingEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()));

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiToolFailureCodes.EffectDenied);
    }

    [Fact]
    public async Task An_approval_delegation_to_a_tool_that_does_not_opt_in_is_audited_with_its_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new FixedEffectPolicy(NhAiEffectDecisionKind.ConsumerAuthoritativeApproval),
            new MissingEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()));

        var result = await InvokeAsync(invoker, Tool with { Approval = NhAiApprovalRequirement.Required });

        var record = AssertRefused(result, audit, NhAiToolFailureCodes.ApprovalDelegationInvalid);
        Assert.Equal("approval-delegation-invalid", record.ApprovalCode);
    }

    private static async Task<TaskResult<string>> InvokeAsync(NhAiToolInvoker invoker, NhAiToolDescriptor descriptor)
    {
        var executed = false;
        var result = await invoker.InvokeAsync(
            descriptor,
            new OrderInput("order-1"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("done"));
            },
            CancellationToken.None);

        Assert.False(executed);
        return result;
    }

    private static NhAiAuditRecord AssertRefused(TaskResult result, NhAiCapturedAuditSink audit, string code)
    {
        Assert.False(result.Success);
        Assert.Equal(code, result.GetResultItems().Select(item => item.Name).First(name => !string.IsNullOrWhiteSpace(name)));
        var record = Assert.Single(audit.Records);
        Assert.Equal(NhAiOutcomeKind.AuthorizationDenied, record.Outcome);
        Assert.Equal(code, record.ResultCode);
        return record;
    }

    private static readonly NhAiInvocationContext Context = new(
        "agent-1",
        "order-maintenance",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AccountableOwnerId = "owner-1"
    };

    private static readonly NhAiToolDescriptor Tool = new(
        "orders.cancel",
        1,
        "Cancel an order.",
        typeof(OrderInput),
        typeof(string),
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Agent,
        true,
        []);

    private sealed record OrderInput(string OrderId);

    private sealed class MissingEvidenceProvider : INhAiApprovalEvidenceProvider
    {
        public ValueTask<NhAiApprovalEvidence?> GetAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiApprovalEvidence?>(null);
        }
    }

    private sealed class FixedEffectPolicy(NhAiEffectDecisionKind kind) : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(kind, "test-decision"));
        }
    }
}
