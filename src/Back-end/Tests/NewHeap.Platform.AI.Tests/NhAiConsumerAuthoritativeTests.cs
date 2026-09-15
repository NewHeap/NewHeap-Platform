using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiConsumerAuthoritativeTests
{
    [Fact]
    public async Task Consumer_authoritative_tool_runs_without_platform_evidence_or_lease_and_audits_the_delegation()
    {
        var audit = new NhAiCapturedAuditSink();
        var executionCount = 0;
        // The default constructor wires the deny idempotency manager and deny approval
        // evidence provider: any attempt to acquire a Platform lease or approval fails.
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new NhAiTestBudgetManager());

        var first = await invoker.InvokeAsync(
            Descriptor,
            new ReceiptInput(OrderId, "grant-1", "key-1"),
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(TaskResult<Receipt>.Succeeded(
                    new Receipt("executed", "status-updated", false)));
            });
        var replay = await invoker.InvokeAsync(
            Descriptor,
            new ReceiptInput(OrderId, "grant-1", "key-1"),
            (_, _) =>
            {
                executionCount++;
                return Task.FromResult(TaskResult<Receipt>.Succeeded(
                    new Receipt("executed", "status-updated", true)));
            });

        Assert.True(first.Success);
        Assert.True(replay.Success);
        Assert.True(replay.Data.IdempotentReplay);
        Assert.Equal(2, executionCount);
        Assert.All(audit.Records, record =>
        {
            Assert.Equal(NhAiOutcomeKind.Succeeded, record.Outcome);
            Assert.Equal("consumer-authoritative", record.ApprovalCode);
            Assert.Equal("consumer-authoritative", record.IdempotencyCode);
            Assert.Null(record.ResultCode);
        });
        Assert.Equal(2, audit.Records.Count);
    }

    [Fact]
    public async Task Consumer_authoritative_tool_returns_its_typed_denial_and_audits_the_result_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new NhAiTestBudgetManager());
        var denial = new Receipt("deny", "approval-invalid-expired-or-replayed", false);

        var result = await invoker.InvokeAsync(
            Descriptor,
            new ReceiptInput(OrderId, "burned-grant", "key-2"),
            (_, _) => Task.FromResult(
                TaskResult<Receipt>
                    .Failed("approval-invalid-expired-or-replayed", "The approval grant is invalid.")
                    .WithData(denial)));

        Assert.False(result.Success);
        Assert.Same(denial, result.Data);
        var record = Assert.Single(audit.Records);
        Assert.Equal(NhAiOutcomeKind.TerminalFailure, record.Outcome);
        Assert.Equal("consumer-authoritative", record.ApprovalCode);
        Assert.Equal("approval-invalid-expired-or-replayed", record.ResultCode);
    }

    [Fact]
    public async Task Authoritative_evidence_denial_payload_is_copied_into_the_typed_result()
    {
        var audit = new NhAiCapturedAuditSink();
        var executed = false;
        var denial = new Receipt("deny", "approval-invalid-expired-or-replayed", false);
        var invoker = CreateInvoker(
            audit,
            new DenyingEvidenceValidator(denial, "grant-store:42"));

        var result = await invoker.InvokeAsync(
            PlatformApprovalDescriptor,
            new ReceiptInput(OrderId, "expired-grant", "key-3"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<Receipt>.Succeeded(
                    new Receipt("executed", "unsafe", false)));
            });

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Same(denial, result.Data);
        var record = Assert.Single(audit.Records);
        Assert.Equal(NhAiOutcomeKind.AuthorizationDenied, record.Outcome);
        Assert.Equal("approval-invalid-expired-or-replayed", record.ApprovalCode);
        Assert.Equal("grant-store:42", record.ApprovalEvidenceReference);
        Assert.Equal("approval-invalid-expired-or-replayed", record.ResultCode);
    }

    [Fact]
    public async Task Authoritative_evidence_denial_payload_of_the_wrong_type_is_a_programming_error()
    {
        var invoker = CreateInvoker(
            new NhAiCapturedAuditSink(),
            new DenyingEvidenceValidator("not-a-receipt", null));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await invoker.InvokeAsync(
                PlatformApprovalDescriptor,
                new ReceiptInput(OrderId, "expired-grant", "key-4"),
                (_, _) => Task.FromResult(TaskResult<Receipt>.Succeeded(
                    new Receipt("executed", "unsafe", false)))));
    }

    [Fact]
    public async Task Effect_policy_cannot_delegate_approval_to_a_tool_that_does_not_opt_in()
    {
        var audit = new NhAiCapturedAuditSink();
        var executed = false;
        var invoker = new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new DelegatingEffectPolicy(),
            new MissingEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()));

        var result = await invoker.InvokeAsync(
            PlatformApprovalDescriptor,
            new ReceiptInput(OrderId, "grant-5", "key-5"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(TaskResult<Receipt>.Succeeded(
                    new Receipt("executed", "unsafe", false)));
            });

        Assert.False(result.Success);
        Assert.False(executed);
        var record = Assert.Single(audit.Records);
        Assert.Equal(NhAiOutcomeKind.AuthorizationDenied, record.Outcome);
        Assert.Equal("approval-delegation-invalid", record.ApprovalCode);
    }

    [Fact]
    public async Task Platform_validated_authoritative_evidence_records_its_evidence_reference()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(
            audit,
            new AttestingEvidenceValidator());

        var result = await invoker.InvokeAsync(
            PlatformApprovalDescriptor,
            new ReceiptInput(OrderId, "grant-6", "key-6"),
            (_, _) => Task.FromResult(TaskResult<Receipt>.Succeeded(
                new Receipt("executed", "status-updated", false))));

        Assert.True(result.Success);
        var record = Assert.Single(audit.Records);
        Assert.Equal(NhAiOutcomeKind.Succeeded, record.Outcome);
        Assert.Equal("authoritative-evidence-validated", record.ApprovalCode);
        Assert.Equal("grant-store:6", record.ApprovalEvidenceReference);
    }

    private static NhAiToolInvoker CreateInvoker(
        INhAiAuditSink audit,
        INhAiAuthoritativeExecutionEvidenceValidator validator)
    {
        return new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(Context),
            [audit],
            new RequireApprovalPolicy(),
            new MissingEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new AcquireIdempotencyManager(),
            [],
            new NhAiTestCapabilityResolver(),
            new NhAiTestBudgetManager(),
            new AllowConcurrencyLimiter(),
            validator);
    }

    private static readonly Guid OrderId = Guid.Parse("50000000-0000-0000-0000-000000000005");

    private static readonly NhAiInvocationContext Context = new(
        "agent-1",
        "order-maintenance",
        new Dictionary<string, string>())
    {
        ActorKind = NhAiActorKind.Agent,
        AccountableOwnerId = "owner-1",
        IdempotencyKey = "platform-key"
    };

    private static readonly NhAiToolDescriptor Descriptor = new(
        "orders.apply-status-receipt",
        1,
        "Apply an approved status change and return the domain receipt.",
        typeof(ReceiptInput),
        typeof(Receipt),
        NhAiToolEffect.Mutation,
        NhAiToolExposure.Mcp,
        true,
        [])
    {
        Approval = NhAiApprovalRequirement.ConsumerAuthoritative,
        Idempotency = NhAiIdempotencySupport.ConsumerAuthoritative,
        ExportSchema = NhAiToolExportSchema.Flat,
        ContractHash = "contract-v1"
    };

    private static readonly NhAiToolDescriptor PlatformApprovalDescriptor = Descriptor with
    {
        Approval = NhAiApprovalRequirement.Required,
        Idempotency = NhAiIdempotencySupport.Required
    };

    private sealed record ReceiptInput(Guid OrderId, string ApprovalGrant, string IdempotencyKey);

    private sealed record Receipt(string Execution, string Code, bool IdempotentReplay);

    private sealed class DenyingEvidenceValidator(
        object denialPayload,
        string? evidenceReference) : INhAiAuthoritativeExecutionEvidenceValidator
    {
        public ValueTask<TaskResult<NhAiAuthoritativeExecutionEvidence>> ValidateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                TaskResult<NhAiAuthoritativeExecutionEvidence>
                    .Failed("approval-invalid-expired-or-replayed", "The approval grant is invalid.")
                    .WithData(NhAiAuthoritativeExecutionEvidence.Denied(
                        denialPayload,
                        evidenceReference)));
        }
    }

    private sealed class AttestingEvidenceValidator : INhAiAuthoritativeExecutionEvidenceValidator
    {
        public ValueTask<TaskResult<NhAiAuthoritativeExecutionEvidence>> ValidateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(TaskResult<NhAiAuthoritativeExecutionEvidence>.Succeeded(
                new NhAiAuthoritativeExecutionEvidence(true, true, "external-key-6", "grant-store:6")));
        }
    }

    private sealed class RequireApprovalPolicy : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.RequireApproval,
                "test-approval-required"));
        }
    }

    private sealed class DelegatingEffectPolicy : INhAiEffectPolicy
    {
        public ValueTask<NhAiEffectDecision> EvaluateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiEffectDecision(
                NhAiEffectDecisionKind.ConsumerAuthoritativeApproval,
                "test-delegated"));
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
            return ValueTask.FromResult(new NhAiConcurrencyDecision(
                true,
                "test-concurrency-acquired",
                new AsyncLease()));
        }

        private sealed class AsyncLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
