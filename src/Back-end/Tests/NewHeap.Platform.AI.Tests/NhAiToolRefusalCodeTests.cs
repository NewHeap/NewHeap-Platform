using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// Every exit of the invoker that ends a call without success audits the code its caller receives as
/// the audit's result code, so what reads the audit names the real reason rather than the outcome kind.
/// Each test compares the code of the shared tool result with the audited code. The approval,
/// idempotency and verification codes keep the detail of their own step, and the audit stays free of
/// arguments, results and untrusted evidence.
/// </summary>
public sealed class NhAiToolRefusalCodeTests
{
    private static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Fact]
    public async Task A_refusal_of_the_invocation_gate_is_audited_with_the_gates_own_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var gate = new NhAiTestInvocationGate((_, _) => ValueTask.FromResult(
            TaskResult<NhAiInvocationContext>.Failed(NhAiAgentFailureCodes.NotGranted, "The agent is not allowed to use this AI tool.")));
        var invoker = new NhAiToolInvoker(gate, [audit]);

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiAgentFailureCodes.NotGranted);
    }

    [Fact]
    public async Task A_refusal_of_the_invocation_gate_without_a_code_is_audited_as_authorization_denied()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(NhAiTestInvocationGate.Denied("Denied."), [audit]);

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.AuthorizationDenied);
    }

    [Fact]
    public async Task An_input_above_its_limit_is_audited_as_input_too_large()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit);

        var result = await InvokeAsync(invoker, Tool with { MaxInputBytes = 4 });

        AssertRefused(result, audit, NhAiOutcomeKind.TerminalFailure, NhAiToolFailureCodes.InputTooLarge);
    }

    [Fact]
    public async Task A_missing_capability_is_audited_as_capability_denied()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = new NhAiToolInvoker(NhAiTestInvocationGate.Authorized(Context), [audit]);

        var result = await InvokeAsync(invoker, Tool with { RequiredCapabilities = ["orders-manage"] });

        AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.CapabilityDenied);
    }

    [Fact]
    public async Task An_authoritative_denial_without_a_code_is_audited_as_execution_evidence_invalid()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(
            audit,
            evidence: new FixedEvidenceValidator(TaskResult<NhAiAuthoritativeExecutionEvidence>.Failed("The grant was refused.")));

        var result = await InvokeAsync(invoker, Tool);

        var record = AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.ExecutionEvidenceInvalid);
        Assert.Equal("execution-evidence-invalid", record.ApprovalCode);
    }

    [Theory]
    [InlineData(true, false, null, null)]
    [InlineData(false, true, "key-1", null)]
    [InlineData(true, false, null, "too-long")]
    [InlineData(false, true, "key with spaces", "evidence-1")]
    public async Task An_invalid_evidence_shape_is_audited_without_its_reference_or_key(
        bool approvalValidated,
        bool idempotencyKeyValidated,
        string? idempotencyKey,
        string? evidenceReference)
    {
        var audit = new NhAiCapturedAuditSink();
        var reference = evidenceReference == "too-long" ? new string('r', 257) : evidenceReference;
        var invoker = CreateInvoker(
            audit,
            evidence: new FixedEvidenceValidator(TaskResult<NhAiAuthoritativeExecutionEvidence>.Succeeded(
                new NhAiAuthoritativeExecutionEvidence(approvalValidated, idempotencyKeyValidated, idempotencyKey, reference))));

        var result = await InvokeAsync(invoker, Tool);

        var record = AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.ExecutionEvidenceInvalid);
        Assert.Equal("execution-evidence-invalid", record.ApprovalCode);
        Assert.Null(record.ApprovalEvidenceReference);
        Assert.Null(record.IdempotencyCode);
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

        AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.EffectDenied);
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

        var record = AssertRefused(result, audit, NhAiOutcomeKind.AuthorizationDenied, NhAiToolFailureCodes.ApprovalDelegationInvalid);
        Assert.Equal("approval-delegation-invalid", record.ApprovalCode);
    }

    [Fact]
    public async Task A_missing_approval_is_audited_as_approval_required_without_an_approval_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, effect: NhAiEffectDecisionKind.RequireApproval);

        var result = await InvokeAsync(invoker, Tool);

        var record = AssertRefused(result, audit, NhAiOutcomeKind.ApprovalRequired, NhAiToolFailureCodes.ApprovalRequired);
        Assert.Null(record.ApprovalCode);
    }

    [Fact]
    public async Task A_rejected_approval_is_audited_as_approval_invalid_with_the_validators_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(
            audit,
            effect: NhAiEffectDecisionKind.RequireApproval,
            approvalEvidence: new FixedEvidenceProvider(CreateApprovalEvidence()),
            approvalValidator: new RejectingApprovalValidator("approval-expired"));

        var result = await InvokeAsync(invoker, Tool);

        var record = AssertRefused(result, audit, NhAiOutcomeKind.ApprovalRequired, NhAiToolFailureCodes.ApprovalInvalid);
        Assert.Equal("approval-expired", record.ApprovalCode);
    }

    [Fact]
    public async Task An_invalid_remaining_budget_is_audited_as_budget_exhausted()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, context: Context with { RemainingBudget = new NhAiModelBudget(0, 0, 0, null) });

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiOutcomeKind.BudgetExhausted, NhAiToolFailureCodes.BudgetExhausted);
    }

    [Fact]
    public async Task A_denied_budget_reservation_is_audited_as_budget_denied()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, budget: new NhAiTestBudgetManager(allow: false));

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiOutcomeKind.BudgetExhausted, NhAiToolFailureCodes.BudgetDenied);
    }

    [Fact]
    public async Task A_full_concurrency_bound_is_audited_as_concurrency_limited()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, limiter: new FixedConcurrencyLimiter(admit: false));

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiOutcomeKind.Conflict, NhAiToolFailureCodes.ConcurrencyLimited);
    }

    [Fact]
    public async Task An_expired_deadline_is_audited_as_deadline_expired()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, context: Context with { Deadline = DateTimeOffset.UtcNow.AddMinutes(-1) });

        var result = await InvokeAsync(invoker, Tool);

        AssertRefused(result, audit, NhAiOutcomeKind.BudgetExhausted, NhAiToolFailureCodes.DeadlineExpired);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_audited_as_idempotency_key_invalid()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit);

        var result = await InvokeAsync(invoker, Tool with { Idempotency = NhAiIdempotencySupport.Required });

        var record = AssertRefused(result, audit, NhAiOutcomeKind.Conflict, NhAiToolFailureCodes.IdempotencyKeyInvalid);
        Assert.Equal("idempotency-key-invalid", record.IdempotencyCode);
    }

    [Fact]
    public async Task A_duplicate_idempotency_key_is_audited_as_idempotency_denied_with_the_lease_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(
            audit,
            context: Context with { IdempotencyKey = "key-1" },
            idempotency: new FixedIdempotencyManager(NhAiIdempotencyDecisionKind.Duplicate, "idempotency-duplicate"));

        var result = await InvokeAsync(invoker, Tool with { Idempotency = NhAiIdempotencySupport.Required });

        var record = AssertRefused(result, audit, NhAiOutcomeKind.Conflict, NhAiToolFailureCodes.IdempotencyDenied);
        Assert.Equal("idempotency-duplicate", record.IdempotencyCode);
    }

    [Fact]
    public async Task A_result_above_its_limit_is_audited_as_result_too_large()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit);

        var result = await InvokeAsync(invoker, Tool with { MaxResultBytes = 4 }, TaskResult<string>.Succeeded("a result above four bytes"));

        AssertRefused(result, audit, NhAiOutcomeKind.TerminalFailure, NhAiToolFailureCodes.ResultTooLarge);
    }

    [Fact]
    public async Task A_failed_verification_is_audited_as_verification_failed_with_the_verifiers_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit, verifier: new FixedVerifier("order-check", new NhAiVerificationResult(false, "order-still-open", "orders/order-1")));

        var result = await InvokeAsync(invoker, Tool with { VerifierId = "order-check" }, TaskResult<string>.Succeeded("done"));

        var record = AssertRefused(result, audit, NhAiOutcomeKind.TerminalFailure, NhAiToolFailureCodes.VerificationFailed);
        Assert.Equal(("order-still-open", "orders/order-1"), (record.VerificationCode, record.VerificationEvidenceReference));
    }

    [Fact]
    public async Task A_failed_result_without_a_code_is_audited_as_ai_tool_failed_and_with_a_code_as_that_code()
    {
        var uncoded = new NhAiCapturedAuditSink();
        var coded = new NhAiCapturedAuditSink();

        var withoutCode = await InvokeAsync(CreateInvoker(uncoded), Tool, TaskResult<string>.Failed("The order could not be cancelled."));
        var withCode = await InvokeAsync(CreateInvoker(coded), Tool, TaskResult<string>.Failed("order-already-shipped", "The order has shipped."));

        AssertRefused(withoutCode, uncoded, NhAiOutcomeKind.TerminalFailure, NhAiToolFailureCodes.Failed);
        AssertRefused(withCode, coded, NhAiOutcomeKind.TerminalFailure, "order-already-shipped");
    }

    [Fact]
    public async Task An_exception_of_the_tool_propagates_and_is_audited_as_ai_tool_failed()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => invoker.InvokeAsync<string>(
            Tool,
            new OrderInput("order-1"),
            (_, _) => throw new InvalidOperationException("Secret detail that must not reach the audit.")));

        var record = Assert.Single(audit.Records);
        Assert.Equal((NhAiOutcomeKind.TerminalFailure, NhAiToolFailureCodes.Failed), (record.Outcome, record.ResultCode));
    }

    [Fact]
    public async Task A_success_is_audited_without_a_result_code()
    {
        var audit = new NhAiCapturedAuditSink();
        var invoker = CreateInvoker(audit);

        var result = await InvokeAsync(invoker, Tool, TaskResult<string>.Succeeded("done"));

        Assert.True(result.Success);
        var record = Assert.Single(audit.Records);
        Assert.Equal((NhAiOutcomeKind.Succeeded, (string?)null), (record.Outcome, record.ResultCode));
    }

    private static NhAiToolInvoker CreateInvoker(
        NhAiCapturedAuditSink audit,
        NhAiInvocationContext? context = null,
        NhAiEffectDecisionKind effect = NhAiEffectDecisionKind.Allow,
        INhAiApprovalEvidenceProvider? approvalEvidence = null,
        INhAiApprovalValidator? approvalValidator = null,
        INhAiIdempotencyManager? idempotency = null,
        INhAiToolVerifier? verifier = null,
        INhAiBudgetManager? budget = null,
        INhAiToolConcurrencyLimiter? limiter = null,
        INhAiAuthoritativeExecutionEvidenceValidator? evidence = null)
    {
        return new NhAiToolInvoker(
            NhAiTestInvocationGate.Authorized(context ?? Context),
            [audit],
            new FixedEffectPolicy(effect),
            approvalEvidence ?? new MissingEvidenceProvider(),
            approvalValidator ?? new NhAiApprovalValidator(new NhAiProposalFactory()),
            idempotency ?? new FixedIdempotencyManager(NhAiIdempotencyDecisionKind.Acquired, "idempotency-acquired"),
            verifier is null ? [] : [verifier],
            new NhAiTestCapabilityResolver(),
            budget ?? new NhAiTestBudgetManager(),
            limiter ?? new FixedConcurrencyLimiter(admit: true),
            evidence);
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

    private static async Task<TaskResult<string>> InvokeAsync(
        NhAiToolInvoker invoker,
        NhAiToolDescriptor descriptor,
        TaskResult<string> outcome)
    {
        var executed = false;
        var result = await invoker.InvokeAsync(
            descriptor,
            new OrderInput("order-1"),
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(outcome);
            },
            CancellationToken.None);

        Assert.True(executed);
        return result;
    }

    /// <summary>
    /// The call failed with <paramref name="code"/> as its shared tool result names it, and its one
    /// audit record carries the same code as its result code.
    /// </summary>
    private static NhAiAuditRecord AssertRefused(
        TaskResult<string> result,
        NhAiCapturedAuditSink audit,
        NhAiOutcomeKind outcome,
        string code)
    {
        Assert.False(result.Success);
        var shared = NhAiToolResult.Create(result, ResultOptions);
        Assert.False(shared.GetProperty(NhAiToolResult.SuccessPropertyName).GetBoolean());
        Assert.Equal(code, shared.GetProperty(NhAiToolResult.CodePropertyName).GetString());

        var record = Assert.Single(audit.Records);
        Assert.Equal((outcome, code), (record.Outcome, record.ResultCode));
        return record;
    }

    private static NhAiApprovalEvidence CreateApprovalEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var proposal = new NhAiProposalFactory().Create(new NhAiProposalCreateRequest(
            Guid.NewGuid(),
            "run-1",
            NhAiActorKind.Agent,
            "agent-1",
            "owner-1",
            Tool,
            new OrderInput("order-1"),
            [new NhAiProposalTarget("order", "order-1")],
            "Cancel the order.",
            ["order-cancelled"],
            new Dictionary<string, string>(),
            new NhAiActionBudget(1),
            now,
            now.AddMinutes(10)));
        var approval = new NhAiApproval(
            Guid.NewGuid(),
            proposal.ProposalId,
            proposal.ProposalHash,
            "approver-1",
            proposal.Targets,
            proposal.Constraints,
            now,
            now.AddMinutes(5));
        return new NhAiApprovalEvidence(proposal, approval);
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

    private sealed class FixedEvidenceProvider(NhAiApprovalEvidence evidence) : INhAiApprovalEvidenceProvider
    {
        public ValueTask<NhAiApprovalEvidence?> GetAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<NhAiApprovalEvidence?>(evidence);
        }
    }

    private sealed class RejectingApprovalValidator(string code) : INhAiApprovalValidator
    {
        public NhAiApprovalValidationResult Validate(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            NhAiApprovalEvidence evidence,
            DateTimeOffset now)
        {
            return new NhAiApprovalValidationResult(false, code);
        }
    }

    private sealed class FixedEvidenceValidator(TaskResult<NhAiAuthoritativeExecutionEvidence> result)
        : INhAiAuthoritativeExecutionEvidenceValidator
    {
        public ValueTask<TaskResult<NhAiAuthoritativeExecutionEvidence>> ValidateAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(result);
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

    private sealed class FixedIdempotencyManager(NhAiIdempotencyDecisionKind decision, string code) : INhAiIdempotencyManager
    {
        public ValueTask<NhAiIdempotencyLease> AcquireAsync(
            NhAiIdempotencyRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NhAiIdempotencyLease(decision, code, "lease-1"));
        }

        public ValueTask CompleteAsync(
            NhAiIdempotencyLease lease,
            NhAiOutcomeKind outcome,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedVerifier(string id, NhAiVerificationResult verification) : INhAiToolVerifier
    {
        public string Id { get; } = id;

        public ValueTask<NhAiVerificationResult> VerifyAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            object arguments,
            object? executionResult,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(verification);
        }
    }

    private sealed class FixedConcurrencyLimiter(bool admit) : INhAiToolConcurrencyLimiter
    {
        public ValueTask<NhAiConcurrencyDecision> TryAcquireAsync(
            NhAiToolDescriptor descriptor,
            NhAiInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(admit
                ? new NhAiConcurrencyDecision(true, "concurrency-acquired", new Lease())
                : new NhAiConcurrencyDecision(false, "concurrency-limit-reached"));
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
