using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiExecutionGuardTests
{
    [Fact]
    public async Task Required_idempotency_prevents_a_retried_side_effect()
    {
        var manager = new RecordingIdempotencyManager();
        var verifier = new RecordingVerifier(true);
        var invoker = CreateInvoker(manager, verifier);
        var executionCount = 0;
        var arguments = new ChangeInput(ProjectId, "active");

        var first = await invoker.InvokeAsync(
            Descriptor,
            arguments,
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(TaskResult<string>.Succeeded("status:active"));
            });
        var retry = await invoker.InvokeAsync(
            Descriptor,
            arguments,
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe-duplicate"));
            });

        Assert.True(first.Success);
        Assert.False(retry.Success);
        Assert.Equal(1, executionCount);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(NhAiOutcomeKind.Succeeded, manager.CompletedOutcome);
        Assert.Equal(64, manager.FirstRequest!.ArgumentHash.Length);
        Assert.Equal("fence-17", manager.FirstRequest.FencingToken);
    }

    [Fact]
    public async Task Changed_arguments_conflict_with_the_same_idempotency_key()
    {
        var manager = new RecordingIdempotencyManager();
        var invoker = CreateInvoker(manager, new RecordingVerifier(true));
        var executionCount = 0;

        var first = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(TaskResult<string>.Succeeded("status:active"));
            });
        var changed = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "archived"),
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe-conflict"));
            });

        Assert.True(first.Success);
        Assert.False(changed.Success);
        Assert.Equal(1, executionCount);
        Assert.Equal(NhAiIdempotencyDecisionKind.Conflict, manager.LastDecision);
    }

    [Fact]
    public async Task Failed_independent_verification_preserves_the_execution_report()
    {
        var manager = new RecordingIdempotencyManager();
        var verifier = new RecordingVerifier(false);
        var invoker = CreateInvoker(manager, verifier);

        var result = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) => Task.FromResult(TaskResult<string>.Succeeded("accepted-by-target")));

        Assert.False(result.Success);
        Assert.Equal("accepted-by-target", result.Data);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(NhAiOutcomeKind.TerminalFailure, manager.CompletedOutcome);
    }

    [Fact]
    public async Task Missing_required_idempotency_key_fails_before_execution()
    {
        var executed = false;
        var context = Context with
        {
            IdempotencyKey = null
        };
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            [],
            new AllowEffectPolicy(),
            new NoApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new RecordingIdempotencyManager(),
            [new RecordingVerifier(true)]);

        var result = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
    }

    [Fact]
    public async Task Expired_capability_fails_before_idempotency_or_execution()
    {
        var executed = false;
        var manager = new RecordingIdempotencyManager();
        var context = Context with
        {
            Deadline = DateTimeOffset.UtcNow.AddSeconds(-1)
        };
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            [],
            new AllowEffectPolicy(),
            new NoApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            manager,
            [new RecordingVerifier(true)]);

        var result = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Null(manager.FirstRequest);
    }

    [Fact]
    public async Task Revoked_capability_resolution_fails_closed()
    {
        var executed = false;
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [],
            new AllowEffectPolicy(),
            new NoApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new RecordingIdempotencyManager(),
            [new RecordingVerifier(true)],
            new RevokedCapabilityResolver());

        var result = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
    }

    [Fact]
    public async Task Bounded_execution_requires_a_successful_budget_reservation()
    {
        var executed = false;
        var context = Context with
        {
            RemainingBudget = new NhAiModelBudget(100, 100, 1, 0.01m)
        };
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            [],
            new AllowEffectPolicy(),
            new NoApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new RecordingIdempotencyManager(),
            [new RecordingVerifier(true)],
            new PassthroughCapabilityResolver(),
            new DenyBudgetManager());

        var result = await invoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<string>.Succeeded("unsafe"));
            });

        Assert.False(result.Success);
        Assert.False(executed);
    }

    [Fact]
    public async Task Descriptor_concurrency_limit_fails_fast_and_releases_after_completion()
    {
        var manager = new RecordingIdempotencyManager();
        var limiter = new TestConcurrencyLimiter();
        var verifier = new RecordingVerifier(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstContext = Context with { IdempotencyKey = "concurrency-first" };
        var secondContext = Context with { IdempotencyKey = "concurrency-second" };
        var firstInvoker = CreateInvoker(firstContext, manager, verifier, limiter);
        var secondInvoker = CreateInvoker(secondContext, manager, verifier, limiter);

        var firstTask = firstInvoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            async (_, cancellationToken) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return TaskResult<string>.Succeeded("first");
            });
        await entered.Task;

        var concurrent = await secondInvoker.InvokeAsync(
            Descriptor,
            new ChangeInput(ProjectId, "active"),
            (_, _) => Task.FromResult(TaskResult<string>.Succeeded("unsafe-second")));
        release.SetResult();
        var first = await firstTask;

        Assert.True(first.Success);
        Assert.False(concurrent.Success);
    }

    private static NhAiToolInvoker CreateInvoker(
        INhAiIdempotencyManager manager,
        INhAiToolVerifier verifier)
    {
        return new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [],
            new AllowEffectPolicy(),
            new NoApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            manager,
            [verifier],
            new NhAiTestBudgetManager());
    }

    private static NhAiToolInvoker CreateInvoker(
        NhAiInvocationContext context,
        INhAiIdempotencyManager manager,
        INhAiToolVerifier verifier,
        INhAiToolConcurrencyLimiter limiter)
    {
        return new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context),
            [],
            new AllowEffectPolicy(),
            new NoApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            manager,
            [verifier],
            new PassthroughCapabilityResolver(),
            new NhAiTestBudgetManager(),
            limiter);
    }

    private static readonly Guid ProjectId =
        Guid.Parse("40000000-0000-0000-0000-000000000004");

    private static readonly NhAiInvocationContext Context = new(
        "agent-1",
        "project-maintenance",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AccountableOwnerId = "owner-1",
        IdempotencyKey = "proposal-9-project-status",
        FencingToken = "fence-17",
        CapabilityGrants = new HashSet<string>(StringComparer.Ordinal)
        {
            "project-manage"
        }
    };

    private static readonly NhAiToolDescriptor Descriptor = new(
        "projects.change-status",
        1,
        "Change project status.",
        typeof(ChangeInput),
        typeof(string),
        NhAiToolEffect.IdempotentMutation,
        NhAiToolExposure.Local,
        true,
        ["project-manage"])
    {
        Approval = NhAiApprovalRequirement.NotRequired,
        Idempotency = NhAiIdempotencySupport.Required,
        VerifierId = "project-status",
        ContractHash = "contract-v1"
    };

    private sealed record ChangeInput(Guid ProjectId, string Status);

    private sealed class AllowEffectPolicy : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.Allow,
                "test-allow"));
        }
    }

    private sealed class NoApprovalEvidenceProvider : INhAiApprovalEvidenceProvider
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

    private sealed class RecordingIdempotencyManager : INhAiIdempotencyManager
    {
        private string? _argumentHash;

        public NhAiIdempotencyRequest? FirstRequest { get; private set; }
        public NhAiOutcomeKind? CompletedOutcome { get; private set; }
        public NhAiIdempotencyDecisionKind? LastDecision { get; private set; }

        public ValueTask<NhAiIdempotencyLease> AcquireAsync(
            NhAiIdempotencyRequest request,
            CancellationToken cancellationToken = default)
        {
            FirstRequest ??= request;
            NhAiIdempotencyDecisionKind decision;
            if (_argumentHash is null)
            {
                _argumentHash = request.ArgumentHash;
                decision = NhAiIdempotencyDecisionKind.Acquired;
            }
            else if (string.Equals(_argumentHash, request.ArgumentHash, StringComparison.Ordinal))
            {
                decision = NhAiIdempotencyDecisionKind.Duplicate;
            }
            else
            {
                decision = NhAiIdempotencyDecisionKind.Conflict;
            }
            LastDecision = decision;
            return ValueTask.FromResult(new NhAiIdempotencyLease(
                decision,
                decision.ToString().ToLowerInvariant(),
                decision == NhAiIdempotencyDecisionKind.Acquired ? "lease-1" : null));
        }

        public ValueTask CompleteAsync(
            NhAiIdempotencyLease lease,
            NhAiOutcomeKind outcome,
            CancellationToken cancellationToken = default)
        {
            CompletedOutcome = outcome;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingVerifier(bool succeeds) : INhAiToolVerifier
    {
        public string Id => "project-status";
        public int Calls { get; private set; }

        public ValueTask<NhAiVerificationResult> VerifyAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            object? executionResult,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new NhAiVerificationResult(
                succeeds,
                succeeds ? "verified" : "state-mismatch",
                $"project-status:{ProjectId}"));
        }
    }

    private sealed class RevokedCapabilityResolver : INhAiCapabilityResolver
    {
        public ValueTask<NhAiCapabilityResolution> ResolveAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiCapabilityResolution(
                false,
                "capability-revoked",
                []));
        }
    }

    private sealed class PassthroughCapabilityResolver : INhAiCapabilityResolver
    {
        public ValueTask<NhAiCapabilityResolution> ResolveAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiCapabilityResolution(
                true,
                "capabilities-granted",
                []));
        }
    }

    private sealed class DenyBudgetManager : INhAiBudgetManager
    {
        public ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
            NhAiBudgetRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                TaskResult<NhAiBudgetReservation>.Failed("budget-exhausted"));
        }
    }

    private sealed class TestConcurrencyLimiter : INhAiToolConcurrencyLimiter
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            if (!await _semaphore.WaitAsync(0, cancellationToken))
            {
                return new NhAiConcurrencyDecision(false, "concurrency-limit-reached");
            }
            return new NhAiConcurrencyDecision(
                true,
                "concurrency-acquired",
                new TestLease(_semaphore));
        }

        private sealed class TestLease(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                semaphore.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
