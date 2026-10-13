using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// An agent scope that requires approval for effects, as a consulted agent runs in: a tool other than
/// a read never runs without an approval, whatever the registered effect policy decides.
/// </summary>
public sealed class NhAiAgentScopeApprovalTests
{
    [Fact]
    public async Task An_effect_the_policy_allows_needs_an_approval_inside_the_scope()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit);
        var executions = 0;

        var outside = await InvokeAsync(invoker, Mutation, () => executions++);
        TaskResult<string> inside;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: true)))
        {
            inside = await InvokeAsync(invoker, Mutation, () => executions++);
        }

        Assert.True(outside.Success);
        Assert.False(inside.Success);
        Assert.Contains(inside.GetResultItems(), item => item.Name == NhAiToolFailureCodes.ApprovalRequired);
        Assert.Equal(1, executions);
        Assert.Equal(NhAiOutcomeKind.ApprovalRequired, audit.Records[^1].Outcome);
    }

    [Fact]
    public async Task A_read_still_runs_and_a_scope_without_the_flag_keeps_the_policy_decision()
    {
        var invoker = CreateInvoker(new NhAiCapturedAuditSink());
        var executions = 0;

        TaskResult<string> read;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: true)))
        {
            read = await InvokeAsync(invoker, Mutation with { Effect = NhAiToolEffect.ReadOnly }, () => executions++);
        }

        TaskResult<string> unflagged;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: false)))
        {
            unflagged = await InvokeAsync(invoker, Mutation, () => executions++);
        }

        Assert.True(read.Success);
        Assert.True(unflagged.Success);
        Assert.Equal(2, executions);
    }

    [Fact]
    public async Task A_tool_that_authorizes_its_own_approval_cannot_run_inside_the_scope()
    {
        var invoker = CreateInvoker(new NhAiCapturedAuditSink());
        var executions = 0;

        TaskResult<string> issuer;
        TaskResult<string> consumerAuthoritative;
        using (NhAiAgentInvocationScopes.Begin(Scope(requireApproval: true)))
        {
            issuer = await InvokeAsync(invoker, Mutation with { Approval = NhAiApprovalRequirement.Issuer }, () => executions++);
            consumerAuthoritative = await InvokeAsync(
                invoker,
                Mutation with { Approval = NhAiApprovalRequirement.ConsumerAuthoritative },
                () => executions++);
        }

        Assert.Contains(issuer.GetResultItems(), item => item.Name == NhAiToolFailureCodes.EffectDenied);
        Assert.Contains(consumerAuthoritative.GetResultItems(), item => item.Name == NhAiToolFailureCodes.EffectDenied);
        Assert.Equal(0, executions);
    }

    private static Task<TaskResult<string>> InvokeAsync(NhAiToolInvoker invoker, NhAiToolDescriptor descriptor, Action execute)
    {
        return invoker.InvokeAsync(
            descriptor,
            new TagInput("urgent"),
            (_, _) =>
            {
                execute();
                return Task.FromResult(TaskResult<string>.Succeeded("tagged"));
            });
    }

    private static NhAiToolInvoker CreateInvoker(INhAiAuditSink audit)
    {
        return new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new AllowEverythingPolicy(),
            new MissingEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new AcquireIdempotencyManager(),
            [],
            new NhAiTestCapabilityResolver(),
            new NhAiTestBudgetManager(),
            new AllowConcurrencyLimiter());
    }

    private static NhAiAgentInvocationScope Scope(bool requireApproval)
    {
        return new NhAiAgentInvocationScope(
            "work-preparation",
            "assistant-agent:work-preparation",
            1,
            new NhAiSubjectInfo(new NhAiSubjectRef("project", "p-1"), "global"),
            "person-1",
            "consultation-1",
            "assistant")
        {
            InitiatorActorId = "person-1",
            DelegateFromInitiator = true,
            RequireApprovalForEffects = requireApproval
        };
    }

    private static readonly NhAiInvocationContext Context = new(
        "assistant-agent:work-preparation",
        "assistant",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AccountableOwnerId = "person-1",
        IdempotencyKey = "key-1"
    };

    /// <summary>
    /// An idempotent mutation the effect policy would run without approval.
    /// </summary>
    private static readonly NhAiToolDescriptor Mutation = new(
        "projects.tag",
        1,
        "Tag a project.",
        typeof(TagInput),
        typeof(string),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Agent,
        true,
        [])
    {
        Approval = NhAiApprovalRequirement.NotRequired,
        ContractHash = "contract-v1"
    };

    private sealed record TagInput(string Tag);

    private sealed class AllowEverythingPolicy : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(NhAiEffectDecisionKind.Allow, "test-allow"));
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
