using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// FB82: the governed invoker lets an agent's own work run a tool without approval only with both keys,
/// the tool's in code and an administrator's switch, and only where nothing else requires approval.
/// Without either key, outside an agent's work, in a scope that requires approval for every effect or for
/// a contract that may not allow it, the call stays a proposal and the switches are not even asked.
/// </summary>
public sealed class NhAiWithoutApprovalTests
{
    [Fact]
    public async Task A_live_switch_runs_the_agents_call_without_a_proposal_and_audits_it_with_its_version()
    {
        var audit = new NhAiCapturedAuditSink();
        var switches = new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, 3));
        var invoker = CreateInvoker(audit, switches);
        NhAiInvocationContext? seen = null;

        TaskResult<string> result;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            result = await InvokeAsync(invoker, Allowed, context => seen = context);
        }

        Assert.True(result.Success);
        Assert.Equal(1, switches.Asked);
        Assert.Equal(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, 3), seen!.WithoutApproval);
        var record = audit.Records[^1];
        Assert.Equal(NhAiOutcomeKind.Succeeded, record.Outcome);
        Assert.Equal(NhAiWithoutApproval.ApprovalCode, record.ApprovalCode);
        Assert.Equal(NhAiWithoutApprovalMode.Live, record.WithoutApprovalMode);
        Assert.Equal(3, record.WithoutApprovalSwitchVersion);
    }

    [Fact]
    public async Task A_switch_in_shadow_still_requires_approval_and_records_that_the_call_would_have_run()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Shadow, 2)));
        var executions = 0;

        TaskResult<string> result;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            result = await InvokeAsync(invoker, Allowed, _ => executions++);
        }

        Assert.False(result.Success);
        Assert.Contains(result.GetResultItems(), item => item.Name == NhAiToolFailureCodes.ApprovalRequired);
        Assert.Equal(0, executions);
        var record = audit.Records[^1];
        Assert.Equal(NhAiOutcomeKind.ApprovalRequired, record.Outcome);
        // No approval code: the call is still a missing approval that becomes a proposal.
        Assert.Null(record.ApprovalCode);
        Assert.Equal(NhAiWithoutApprovalMode.Shadow, record.WithoutApprovalMode);
        Assert.Equal(2, record.WithoutApprovalSwitchVersion);
    }

    [Fact]
    public async Task Without_the_code_key_a_live_switch_is_never_asked_and_the_call_needs_approval()
    {
        var audit = new NhAiCapturedAuditSink();
        var switches = new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, 1));
        var invoker = CreateInvoker(audit, switches);
        var executions = 0;

        TaskResult<string> result;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            result = await InvokeAsync(invoker, Allowed with { AllowWithoutApproval = false }, _ => executions++);
        }

        Assert.False(result.Success);
        Assert.Equal(0, executions);
        Assert.Equal(0, switches.Asked);
        Assert.Equal(NhAiWithoutApprovalMode.Off, audit.Records[^1].WithoutApprovalMode);
    }

    public static TheoryData<string> RefusedContracts => new()
    {
        "destructive",
        "external-side-effect",
        "issuer",
        "consumer-authoritative",
        "no-policy",
        "not-agent-exposed",
        "composition",
        "individual-decision"
    };

    [Theory]
    [MemberData(nameof(RefusedContracts))]
    public async Task A_contract_that_may_not_allow_it_keeps_its_approval_and_fails_validation(string contract)
    {
        var descriptor = contract switch
        {
            "destructive" => Allowed with { Effect = NhAiToolEffect.Destructive, VerifierId = "verifier" },
            "external-side-effect" => Allowed with { Effect = NhAiToolEffect.ExternalSideEffect },
            "issuer" => Allowed with { Approval = NhAiApprovalRequirement.Issuer },
            "consumer-authoritative" => Allowed with { Approval = NhAiApprovalRequirement.ConsumerAuthoritative },
            "no-policy" => Allowed with { AuthorizationPolicies = [] },
            "not-agent-exposed" => Allowed with { Exposure = NhAiToolExposure.Local },
            "composition" => Allowed with { CompositionFeature = NhAiCompositionFeature.Compositions },
            _ => Allowed with { RequireIndividualDecision = true }
        };
        var switches = new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, 1));
        var invoker = CreateInvoker(new NhAiCapturedAuditSink(), switches);
        var executions = 0;

        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            await InvokeAsync(invoker, descriptor, _ => executions++);
        }

        Assert.False(NhAiWithoutApproval.IsAllowedBy(descriptor));
        Assert.Throws<InvalidOperationException>(() => NhAiWithoutApproval.Validate(descriptor));
        Assert.Equal(0, executions);
        Assert.Equal(0, switches.Asked);
    }

    [Fact]
    public async Task A_scope_that_requires_approval_for_effects_a_persons_turn_and_another_agent_keep_the_proposal()
    {
        var switches = new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, 1));
        var invoker = CreateInvoker(new NhAiCapturedAuditSink(), switches);
        var executions = 0;

        // A consultation, a rule turn, a shadow run or a call for another object: every effect is a proposal.
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: true)))
        {
            await InvokeAsync(invoker, Allowed, _ => executions++);
        }
        // A person's own turn in the agent's thread is no agent work.
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false) with { IsAgentWork = false }))
        {
            await InvokeAsync(invoker, Allowed, _ => executions++);
        }
        // Outside any agent scope, such as a call of the application itself.
        await InvokeAsync(invoker, Allowed, _ => executions++);
        // A scope of another agent than the one the call runs for.
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false) with { AgentId = "other-agent" }))
        {
            await InvokeAsync(invoker, Allowed, _ => executions++);
        }

        Assert.Equal(0, executions);
        Assert.Equal(0, switches.Asked);
    }

    [Fact]
    public async Task An_effect_policy_that_asks_for_approval_with_its_own_code_keeps_it()
    {
        var switches = new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, 1));
        var invoker = CreateInvoker(new NhAiCapturedAuditSink(), switches, new RequireApprovalPolicy("application-four-eyes"));
        var executions = 0;

        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            await InvokeAsync(invoker, Allowed, _ => executions++);
        }

        Assert.Equal(0, executions);
        Assert.Equal(0, switches.Asked);
    }

    [Fact]
    public async Task A_switch_that_answers_without_a_version_counts_as_off()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, new FixedSwitches(new NhAiWithoutApprovalDecision(NhAiWithoutApprovalMode.Live, null)));
        var executions = 0;

        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            await InvokeAsync(invoker, Allowed, _ => executions++);
        }

        Assert.Equal(0, executions);
        Assert.Equal(NhAiWithoutApprovalMode.Off, audit.Records[^1].WithoutApprovalMode);
    }

    private static Task<TaskResult<string>> InvokeAsync(
        NhAiToolInvoker invoker,
        NhAiToolDescriptor descriptor,
        Action<NhAiInvocationContext> execute)
    {
        return invoker.InvokeAsync(
            descriptor,
            new TaskInput("t-1"),
            (context, _) =>
            {
                execute(context);
                return Task.FromResult(TaskResult<string>.Succeeded("completed"));
            });
    }

    private static NhAiToolInvoker CreateInvoker(
        INhAiAuditSink audit,
        INhAiWithoutApprovalSwitches switches,
        INhAiEffectPolicy? effectPolicy = null)
    {
        return new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            effectPolicy ?? new RequireApprovalPolicy("approval-required"),
            new MissingEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new AcquireIdempotencyManager(),
            [],
            new NhAiTestCapabilityResolver(),
            new NhAiTestBudgetManager(),
            new AllowConcurrencyLimiter(),
            null,
            null,
            TimeProvider.System,
            switches);
    }

    private static NhAiAgentInvocationScope Scope(bool requireApproval)
    {
        return new NhAiAgentInvocationScope(
            "task-keeper",
            "assistant-agent:task-keeper",
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("project", "p-1"), "global"),
            "operations-1",
            "turn-1",
            "assistant")
        {
            Accountability = NhAiAgentAccountability.ServiceAccount,
            RequireApprovalForEffects = requireApproval
        };
    }

    private static readonly NhAiInvocationContext Context = new(
        "assistant-agent:task-keeper",
        "assistant",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AgentId = "task-keeper",
        AccountableOwnerId = "operations-1",
        IdempotencyKey = "key-1",
        IsAgentWork = true
    };

    /// <summary>
    /// A reversible change that the tool allows to run without approval.
    /// </summary>
    private static readonly NhAiToolDescriptor Allowed = new(
        "project-tasks.complete",
        1,
        "Complete a task.",
        typeof(TaskInput),
        typeof(string),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Agent,
        true,
        ["projects.manage"])
    {
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required,
        ContractHash = "contract-v1",
        AllowWithoutApproval = true
    };

    private sealed record TaskInput(string TaskId);

    private sealed class FixedSwitches(NhAiWithoutApprovalDecision decision) : INhAiWithoutApprovalSwitches
    {
        public int Asked { get; private set; }

        public ValueTask<NhAiWithoutApprovalDecision> DecideAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            Asked++;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class RequireApprovalPolicy(string code) : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(NhAiEffectDecisionKind.RequireApproval, code));
        }
    }

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

    private sealed class AcquireIdempotencyManager : INhAiIdempotencyManager
    {
        public ValueTask<NhAiIdempotencyLease> AcquireAsync(
            NhAiIdempotencyRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiIdempotencyLease(
                NhAiIdempotencyDecisionKind.Acquired,
                "acquired",
                "lease-1"));
        }

        public ValueTask CompleteAsync(
            NhAiIdempotencyLease lease,
            NhAiOutcomeKind outcome,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AllowConcurrencyLimiter : INhAiToolConcurrencyLimiter
    {
        public ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiConcurrencyDecision(true, "test-concurrency-acquired", new Lease()));
        }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
