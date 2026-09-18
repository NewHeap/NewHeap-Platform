using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

public sealed partial class NhAiToolInvoker : INhAiToolInvoker
{
    public const string ActivitySourceName = "NewHeap.Platform.AI";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly INhAiToolInvocationGate _invocationGate;
    private readonly IReadOnlyList<INhAiAuditSink> _auditSinks;
    private readonly INhAiEffectPolicy _effectPolicy;
    private readonly INhAiApprovalEvidenceProvider _approvalEvidenceProvider;
    private readonly INhAiApprovalValidator _approvalValidator;
    private readonly INhAiAuthoritativeExecutionEvidenceValidator _authoritativeEvidenceValidator;
    private readonly INhAiIdempotencyManager _idempotencyManager;
    private readonly IReadOnlyDictionary<string, INhAiToolVerifier> _verifiers;
    private readonly INhAiCapabilityResolver _capabilityResolver;
    private readonly INhAiBudgetManager _budgetManager;
    private readonly INhAiToolConcurrencyLimiter _concurrencyLimiter;
    private readonly ILogger? _logger;

    public NhAiToolInvoker(INhAiToolInvocationGate invocationGate)
        : this(
            invocationGate,
            [],
            new NhAiDefaultEffectPolicy(),
            new NhAiDenyApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new NhAiDenyIdempotencyManager(),
            [],
            new NhAiInvocationContextCapabilityResolver(),
            new NhAiDenyBudgetManager(),
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        INhAiBudgetManager budgetManager)
        : this(
            invocationGate,
            [],
            new NhAiDefaultEffectPolicy(),
            new NhAiDenyApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new NhAiDenyIdempotencyManager(),
            [],
            new NhAiInvocationContextCapabilityResolver(),
            budgetManager,
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks)
        : this(
            invocationGate,
            auditSinks,
            new NhAiDefaultEffectPolicy(),
            new NhAiDenyApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new NhAiDenyIdempotencyManager(),
            [],
            new NhAiInvocationContextCapabilityResolver(),
            new NhAiDenyBudgetManager(),
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiBudgetManager budgetManager)
        : this(
            invocationGate,
            auditSinks,
            new NhAiDefaultEffectPolicy(),
            new NhAiDenyApprovalEvidenceProvider(),
            new NhAiApprovalValidator(new NhAiProposalFactory()),
            new NhAiDenyIdempotencyManager(),
            [],
            new NhAiInvocationContextCapabilityResolver(),
            budgetManager,
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiEffectPolicy effectPolicy,
        INhAiApprovalEvidenceProvider approvalEvidenceProvider,
        INhAiApprovalValidator approvalValidator)
        : this(
            invocationGate,
            auditSinks,
            effectPolicy,
            approvalEvidenceProvider,
            approvalValidator,
            new NhAiDenyIdempotencyManager(),
            [],
            new NhAiInvocationContextCapabilityResolver(),
            new NhAiDenyBudgetManager(),
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiEffectPolicy effectPolicy,
        INhAiApprovalEvidenceProvider approvalEvidenceProvider,
        INhAiApprovalValidator approvalValidator,
        INhAiIdempotencyManager idempotencyManager,
        IEnumerable<INhAiToolVerifier> verifiers)
        : this(
            invocationGate,
            auditSinks,
            effectPolicy,
            approvalEvidenceProvider,
            approvalValidator,
            idempotencyManager,
            verifiers,
            new NhAiInvocationContextCapabilityResolver(),
            new NhAiDenyBudgetManager(),
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiEffectPolicy effectPolicy,
        INhAiApprovalEvidenceProvider approvalEvidenceProvider,
        INhAiApprovalValidator approvalValidator,
        INhAiIdempotencyManager idempotencyManager,
        IEnumerable<INhAiToolVerifier> verifiers,
        INhAiBudgetManager budgetManager)
        : this(
            invocationGate,
            auditSinks,
            effectPolicy,
            approvalEvidenceProvider,
            approvalValidator,
            idempotencyManager,
            verifiers,
            new NhAiInvocationContextCapabilityResolver(),
            budgetManager,
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiEffectPolicy effectPolicy,
        INhAiApprovalEvidenceProvider approvalEvidenceProvider,
        INhAiApprovalValidator approvalValidator,
        INhAiIdempotencyManager idempotencyManager,
        IEnumerable<INhAiToolVerifier> verifiers,
        INhAiCapabilityResolver capabilityResolver)
        : this(
            invocationGate,
            auditSinks,
            effectPolicy,
            approvalEvidenceProvider,
            approvalValidator,
            idempotencyManager,
            verifiers,
            capabilityResolver,
            new NhAiDenyBudgetManager(),
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiEffectPolicy effectPolicy,
        INhAiApprovalEvidenceProvider approvalEvidenceProvider,
        INhAiApprovalValidator approvalValidator,
        INhAiIdempotencyManager idempotencyManager,
        IEnumerable<INhAiToolVerifier> verifiers,
        INhAiCapabilityResolver capabilityResolver,
        INhAiBudgetManager budgetManager)
        : this(
            invocationGate,
            auditSinks,
            effectPolicy,
            approvalEvidenceProvider,
            approvalValidator,
            idempotencyManager,
            verifiers,
            capabilityResolver,
            budgetManager,
            new NhAiInProcessToolConcurrencyLimiter())
    {
    }

    public NhAiToolInvoker(
        INhAiToolInvocationGate invocationGate,
        IEnumerable<INhAiAuditSink> auditSinks,
        INhAiEffectPolicy effectPolicy,
        INhAiApprovalEvidenceProvider approvalEvidenceProvider,
        INhAiApprovalValidator approvalValidator,
        INhAiIdempotencyManager idempotencyManager,
        IEnumerable<INhAiToolVerifier> verifiers,
        INhAiCapabilityResolver capabilityResolver,
        INhAiBudgetManager budgetManager,
        INhAiToolConcurrencyLimiter concurrencyLimiter,
        INhAiAuthoritativeExecutionEvidenceValidator? authoritativeEvidenceValidator = null,
        ILogger<NhAiToolInvoker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(invocationGate);
        ArgumentNullException.ThrowIfNull(auditSinks);
        ArgumentNullException.ThrowIfNull(effectPolicy);
        ArgumentNullException.ThrowIfNull(approvalEvidenceProvider);
        ArgumentNullException.ThrowIfNull(approvalValidator);
        ArgumentNullException.ThrowIfNull(idempotencyManager);
        ArgumentNullException.ThrowIfNull(verifiers);
        ArgumentNullException.ThrowIfNull(capabilityResolver);
        ArgumentNullException.ThrowIfNull(budgetManager);
        ArgumentNullException.ThrowIfNull(concurrencyLimiter);
        _invocationGate = invocationGate;
        _auditSinks = auditSinks.ToArray();
        _effectPolicy = effectPolicy;
        _approvalEvidenceProvider = approvalEvidenceProvider;
        _approvalValidator = approvalValidator;
        _authoritativeEvidenceValidator = authoritativeEvidenceValidator
            ?? new NhAiNoAuthoritativeExecutionEvidenceValidator();
        _idempotencyManager = idempotencyManager;
        _verifiers = CreateVerifierRegistry(verifiers);
        _capabilityResolver = capabilityResolver;
        _budgetManager = budgetManager;
        _concurrencyLimiter = concurrencyLimiter;
        _logger = logger;
    }

    public async Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        CancellationToken cancellationToken = default)
    {
        return await InvokeAsync(
            descriptor,
            NhAiNoArguments.Instance,
            invocation,
            cancellationToken);
    }

    public async Task<TaskResult<T>> InvokeAsync<T>(
        NhAiToolDescriptor descriptor,
        object arguments,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(invocation);

        // From here on an exception belongs to the governed invocation, not to argument binding.
        NhAiToolArguments.MarkInvokerEntered();
        var trace = new InvocationTrace();
        var result = await InvokeGovernedAsync(
            descriptor,
            arguments,
            invocation,
            trace,
            cancellationToken);

        NhAiToolOutcomeCapture.Record(result, trace.EvidenceReference);
        return result;
    }

    private async Task<TaskResult<T>> InvokeGovernedAsync<T>(
        NhAiToolDescriptor descriptor,
        object arguments,
        Func<NhAiInvocationContext, CancellationToken, Task<TaskResult<T>>> invocation,
        InvocationTrace trace,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("ai.tool.invoke");
        activity?.SetTag("newheap.ai.tool.id", descriptor.Id);
        activity?.SetTag("newheap.ai.tool.version", descriptor.Version);
        activity?.SetTag("newheap.ai.tool.effect", descriptor.Effect.ToString());
        activity?.SetTag("newheap.ai.tool.exposure", descriptor.Exposure.ToString());

        var authorization = await _invocationGate.AuthorizeAsync(descriptor, cancellationToken);
        if (!authorization.Success)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "denied");
            await WriteAuditAsync(
                descriptor,
                null,
                NhAiOutcomeKind.AuthorizationDenied,
                cancellationToken);
            return WithFallbackCode(
                TaskResult<T>.Failed(authorization),
                NhAiToolFailureCodes.AuthorizationDenied);
        }

        var context = authorization.Data;
        if (ExceedsInputLimit(arguments, descriptor.MaxInputBytes))
        {
            activity?.SetTag("newheap.ai.tool.outcome", "input-too-large");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.TerminalFailure,
                cancellationToken);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.InputTooLarge,
                "AI tool input exceeded its configured size limit.");
        }

        var capabilityResolution = await _capabilityResolver.ResolveAsync(
            descriptor,
            context,
            DateTimeOffset.UtcNow,
            cancellationToken);
        activity?.SetTag(
            "newheap.ai.tool.capability_decision",
            SafeCode(capabilityResolution.Code));
        if (!capabilityResolution.Succeeded)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "capability-denied");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.AuthorizationDenied,
                cancellationToken);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.CapabilityDenied,
                "The AI invocation lacks a required tool capability.");
        }

        var authoritativeEvidence = await _authoritativeEvidenceValidator.ValidateAsync(
            descriptor,
            context,
            arguments,
            cancellationToken);
        if (!authoritativeEvidence.Success)
        {
            var denial = authoritativeEvidence.Data;
            var denialCode = FirstResultCode(authoritativeEvidence);
            activity?.SetTag("newheap.ai.tool.outcome", "execution-evidence-invalid");
            activity?.SetTag("newheap.ai.tool.approval_code", denialCode);
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.AuthorizationDenied,
                cancellationToken,
                approvalCode: denialCode ?? "execution-evidence-invalid",
                approvalEvidenceReference: SafeEvidenceReference(denial?.EvidenceReference),
                resultCode: denialCode);

            trace.EvidenceReference = SafeEvidenceReference(denial?.EvidenceReference);
            var deniedResult = WithFallbackCode(
                TaskResult<T>.Failed(authoritativeEvidence),
                NhAiToolFailureCodes.ExecutionEvidenceInvalid);
            if (denial?.DenialPayload is { } denialPayload)
            {
                if (denialPayload is not T typedDenialPayload)
                {
                    throw new InvalidOperationException(
                        $"AI tool '{descriptor.Id}' received an authoritative denial payload of type '{denialPayload.GetType()}' that is not assignable to '{typeof(T)}'.");
                }
                deniedResult.WithData(typedDenialPayload);
            }
            return deniedResult;
        }
        var validatedEvidence = authoritativeEvidence.Data;
        string? approvalCode = null;
        string? approvalEvidenceReference = null;
        if ((validatedEvidence.ApprovalValidated || validatedEvidence.IdempotencyKeyValidated)
                && string.IsNullOrWhiteSpace(validatedEvidence.EvidenceReference)
            || validatedEvidence.EvidenceReference is { Length: > 256 }
            || (validatedEvidence.IdempotencyKeyValidated
                && (string.IsNullOrWhiteSpace(validatedEvidence.IdempotencyKey)
                    || validatedEvidence.IdempotencyKey.Length > 256
                    || validatedEvidence.IdempotencyKey.Any(character =>
                        !char.IsAsciiLetterOrDigit(character)
                        && character is not '-' and not '_' and not '.' and not ':'))))
        {
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.ExecutionEvidenceInvalid,
                "Authoritative AI execution evidence is invalid.");
        }
        if (validatedEvidence.IdempotencyKeyValidated)
        {
            context = context with { IdempotencyKey = validatedEvidence.IdempotencyKey };
        }
        if (validatedEvidence.ApprovalValidated)
        {
            approvalCode = "authoritative-evidence-validated";
            approvalEvidenceReference = SafeEvidenceReference(validatedEvidence.EvidenceReference);
        }

        var effectDecision = await _effectPolicy.EvaluateAsync(
            descriptor,
            context,
            cancellationToken);
        activity?.SetTag("newheap.ai.tool.effect_decision", effectDecision.Code);
        if (effectDecision.Kind == NhAiEffectDecisionKind.Deny)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "effect-denied");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.AuthorizationDenied,
                cancellationToken);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.EffectDenied,
                "AI tool effect policy denied execution.");
        }
        if (effectDecision.Kind == NhAiEffectDecisionKind.ConsumerAuthoritativeApproval)
        {
            if (descriptor.Approval != NhAiApprovalRequirement.ConsumerAuthoritative)
            {
                activity?.SetTag("newheap.ai.tool.outcome", "approval-delegation-invalid");
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.AuthorizationDenied,
                    cancellationToken,
                    approvalCode: "approval-delegation-invalid");
                return TaskResult<T>.Failed(
                    NhAiToolFailureCodes.ApprovalDelegationInvalid,
                    "AI tool effect policy delegated approval to a tool that does not declare consumer-authoritative approval.");
            }
            if (!validatedEvidence.ApprovalValidated)
            {
                approvalCode = "consumer-authoritative";
            }
        }
        else if (effectDecision.Kind == NhAiEffectDecisionKind.RequireApproval
            && !validatedEvidence.ApprovalValidated)
        {
            var evidence = await _approvalEvidenceProvider.GetAsync(
                descriptor,
                context,
                arguments,
                cancellationToken);
            if (evidence is null)
            {
                activity?.SetTag("newheap.ai.tool.outcome", "approval-required");
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.ApprovalRequired,
                    cancellationToken);
                return TaskResult<T>.Failed(
                    NhAiToolFailureCodes.ApprovalRequired,
                    "AI tool approval is required.");
            }

            var validation = _approvalValidator.Validate(
                descriptor,
                context,
                arguments,
                evidence,
                DateTimeOffset.UtcNow);
            if (!validation.Succeeded)
            {
                activity?.SetTag("newheap.ai.tool.outcome", "approval-invalid");
                activity?.SetTag("newheap.ai.tool.approval_code", validation.Code);
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.ApprovalRequired,
                    cancellationToken,
                    approvalCode: SafeCode(validation.Code) ?? "approval-invalid");
                return TaskResult<T>.Failed(
                    NhAiToolFailureCodes.ApprovalInvalid,
                    $"AI tool approval validation failed with code '{validation.Code}'.");
            }
            approvalCode = SafeCode(validation.Code) ?? "approval-validated";
        }
        if (descriptor.Approval == NhAiApprovalRequirement.Issuer && approvalCode is null)
        {
            // An issuer creates approval artifacts under the consumer's own authorization.
            approvalCode = "issuer";
        }

        if (context.RemainingBudget is { } remainingBudget
            && (remainingBudget.MaxCalls < 1
                || remainingBudget.MaxInputTokens < 0
                || remainingBudget.MaxOutputTokens < 0
                || remainingBudget.MaxEstimatedCost < 0))
        {
            activity?.SetTag("newheap.ai.tool.outcome", "budget-invalid");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.BudgetExhausted,
                cancellationToken,
                approvalCode: approvalCode,
                approvalEvidenceReference: approvalEvidenceReference);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.BudgetExhausted,
                "AI tool execution budget is exhausted.");
        }

        var reservation = await _budgetManager.ReserveAsync(
            new NhAiBudgetRequest(
                context.InvocationId,
                context.ModelProfileName ?? "tool-execution",
                1,
                0,
                0,
                null)
            {
                ActorId = context.AccountableOwnerId ?? context.ActorId
            },
            cancellationToken);
        if (!reservation.Success)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "budget-denied");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.BudgetExhausted,
                cancellationToken,
                approvalCode: approvalCode,
                approvalEvidenceReference: approvalEvidenceReference);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.BudgetDenied,
                "AI tool execution budget could not be reserved.");
        }
        activity?.SetTag("newheap.ai.tool.budget", "reserved");

        var concurrency = await _concurrencyLimiter.TryAcquireAsync(
            descriptor,
            context,
            cancellationToken);
        activity?.SetTag(
            "newheap.ai.tool.concurrency",
            SafeCode(concurrency.Code));
        if (!concurrency.Acquired || concurrency.Lease is null)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "concurrency-denied");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.Conflict,
                cancellationToken,
                approvalCode: approvalCode,
                approvalEvidenceReference: approvalEvidenceReference);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.ConcurrencyLimited,
                "AI tool concurrency limit was reached.");
        }
        await using var concurrencyLease = concurrency.Lease;

        var executionTimeout = GetExecutionTimeout(descriptor, context);
        if (executionTimeout <= TimeSpan.Zero)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "deadline-expired");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.BudgetExhausted,
                cancellationToken,
                approvalCode: approvalCode,
                approvalEvidenceReference: approvalEvidenceReference);
            return TaskResult<T>.Failed(
                NhAiToolFailureCodes.DeadlineExpired,
                "AI tool execution deadline has expired.");
        }

        NhAiIdempotencyLease? idempotencyLease = null;
        var idempotencyCompleted = false;
        string? idempotencyCode = null;
        if (descriptor.Idempotency == NhAiIdempotencySupport.ConsumerAuthoritative)
        {
            // The consuming engine reconciles replays itself and returns its receipt;
            // the invoker records the delegation instead of acquiring a Platform lease.
            idempotencyCode = "consumer-authoritative";
            activity?.SetTag("newheap.ai.tool.idempotency", idempotencyCode);
        }
        else if (descriptor.Idempotency == NhAiIdempotencySupport.Required
            || (descriptor.Idempotency == NhAiIdempotencySupport.Supported
                && !string.IsNullOrWhiteSpace(context.IdempotencyKey)))
        {
            if (string.IsNullOrWhiteSpace(context.IdempotencyKey)
                || context.IdempotencyKey.Length > 256)
            {
                activity?.SetTag("newheap.ai.tool.outcome", "idempotency-key-invalid");
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.Conflict,
                    cancellationToken,
                    approvalCode: approvalCode,
                    approvalEvidenceReference: approvalEvidenceReference,
                    idempotencyCode: "idempotency-key-invalid");
                return TaskResult<T>.Failed(
                    NhAiToolFailureCodes.IdempotencyKeyInvalid,
                    "AI tool execution requires a valid idempotency key.");
            }

            idempotencyLease = await _idempotencyManager.AcquireAsync(
                new NhAiIdempotencyRequest(
                    context.InvocationId,
                    descriptor.Id,
                    descriptor.Version,
                    context.ActorId,
                    context.IdempotencyKey,
                    NhAiCanonicalJson.ComputeHash(arguments),
                    context.FencingToken),
                cancellationToken);
            idempotencyCode = SafeCode(idempotencyLease.Code);
            activity?.SetTag("newheap.ai.tool.idempotency", idempotencyCode);
            if (idempotencyLease.Decision != NhAiIdempotencyDecisionKind.Acquired)
            {
                activity?.SetTag("newheap.ai.tool.outcome", "idempotency-denied");
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.Conflict,
                    cancellationToken,
                    approvalCode: approvalCode,
                    approvalEvidenceReference: approvalEvidenceReference,
                    idempotencyCode: idempotencyCode);
                return TaskResult<T>.Failed(
                    NhAiToolFailureCodes.IdempotencyDenied,
                    "AI tool idempotency policy denied execution.");
            }
        }

        async ValueTask CompleteIdempotencyOnceAsync(NhAiOutcomeKind outcome)
        {
            if (idempotencyLease is null || idempotencyCompleted)
            {
                return;
            }
            idempotencyCompleted = true;
            await _idempotencyManager.CompleteAsync(
                idempotencyLease,
                outcome,
                CancellationToken.None);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(executionTimeout);
        string? verificationCode = null;
        string? verificationEvidenceReference = null;
        try
        {
            var result = await invocation(context, timeout.Token);
            if (result.Success && ExceedsResultLimit(result.Data, descriptor.MaxResultBytes))
            {
                await CompleteIdempotencyOnceAsync(NhAiOutcomeKind.TerminalFailure);
                activity?.SetTag("newheap.ai.tool.outcome", "result-too-large");
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.TerminalFailure,
                    cancellationToken,
                    approvalCode: approvalCode,
                    approvalEvidenceReference: approvalEvidenceReference,
                    idempotencyCode: idempotencyCode);
                return TaskResult<T>.Failed(
                    NhAiToolFailureCodes.ResultTooLarge,
                    "AI tool result exceeded its configured size limit.");
            }

            if (result.Success && !string.IsNullOrWhiteSpace(descriptor.VerifierId))
            {
                if (!_verifiers.TryGetValue(descriptor.VerifierId, out var verifier))
                {
                    throw new InvalidOperationException(
                        $"AI tool '{descriptor.Id}' references unregistered verifier '{descriptor.VerifierId}'.");
                }

                var verification = await verifier.VerifyAsync(
                    descriptor,
                    context,
                    arguments,
                    result.Data,
                    timeout.Token);
                verificationCode = SafeCode(verification.Code);
                verificationEvidenceReference = SafeEvidenceReference(
                    verification.EvidenceReference);
                activity?.SetTag("newheap.ai.tool.verification", verificationCode);
                if (!verification.Succeeded)
                {
                    await CompleteIdempotencyOnceAsync(NhAiOutcomeKind.TerminalFailure);
                    activity?.SetTag("newheap.ai.tool.outcome", "verification-failed");
                    await WriteAuditAsync(
                        descriptor,
                        context,
                        NhAiOutcomeKind.TerminalFailure,
                        cancellationToken,
                        approvalCode: approvalCode,
                        approvalEvidenceReference: approvalEvidenceReference,
                        idempotencyCode: idempotencyCode,
                        verificationCode: verificationCode,
                        verificationEvidenceReference: verificationEvidenceReference);
                    trace.EvidenceReference = verificationEvidenceReference;
                    return TaskResult<T>
                        .Failed(
                            NhAiToolFailureCodes.VerificationFailed,
                            "AI tool execution completed, but independent verification failed.")
                        .WithExecutionData(result.Data);
                }
            }

            var outcome = result.Success
                ? NhAiOutcomeKind.Succeeded
                : NhAiOutcomeKind.TerminalFailure;
            var resultCode = result.Success ? null : FirstResultCode(result);
            await CompleteIdempotencyOnceAsync(outcome);
            activity?.SetTag("newheap.ai.tool.outcome", result.Success ? "succeeded" : "failed");
            activity?.SetTag("newheap.ai.tool.result_code", resultCode);
            await WriteAuditAsync(
                descriptor,
                context,
                outcome,
                cancellationToken,
                approvalCode: approvalCode,
                approvalEvidenceReference: approvalEvidenceReference,
                idempotencyCode: idempotencyCode,
                verificationCode: verificationCode,
                verificationEvidenceReference: verificationEvidenceReference,
                resultCode: resultCode);
            return result;
        }
        catch (OperationCanceledException)
        {
            await CompleteIdempotencyOnceAsync(NhAiOutcomeKind.TerminalFailure);
            activity?.SetTag("newheap.ai.tool.outcome", "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            await CompleteIdempotencyOnceAsync(NhAiOutcomeKind.TerminalFailure);
            activity?.SetTag("newheap.ai.tool.outcome", "exception");
            if (_logger is not null)
            {
                // Content-free: the identity of the tool and the exception type, never its message.
                LogUnexpectedException(
                    _logger,
                    descriptor.Id,
                    descriptor.Version,
                    context.InvocationId,
                    exception.GetType().FullName ?? exception.GetType().Name);
            }
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.TerminalFailure,
                CancellationToken.None,
                approvalCode: approvalCode,
                approvalEvidenceReference: approvalEvidenceReference,
                idempotencyCode: idempotencyCode);
            throw;
        }
    }

    private async ValueTask WriteAuditAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext? context,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken,
        string? approvalCode = null,
        string? approvalEvidenceReference = null,
        string? idempotencyCode = null,
        string? verificationCode = null,
        string? verificationEvidenceReference = null,
        string? resultCode = null)
    {
        if (_auditSinks.Count == 0)
        {
            return;
        }

        var record = new NhAiAuditRecord(
            context?.InvocationId ?? Guid.NewGuid(),
            descriptor.Id,
            descriptor.Version,
            context?.ActorId,
            NhAiNames.IsSegment(context?.Purpose) ? context!.Purpose : null,
            outcome,
            DateTimeOffset.UtcNow)
        {
            RunId = context?.RunId,
            CorrelationId = context?.CorrelationId,
            ApprovalId = context?.ApprovalId,
            ApprovalCode = approvalCode,
            ApprovalEvidenceReference = approvalEvidenceReference,
            IdempotencyCode = idempotencyCode,
            VerificationCode = verificationCode,
            VerificationEvidenceReference = verificationEvidenceReference,
            ResultCode = resultCode,
            AnnotationOverrides = NhAiToolAnnotationHints.DescribeOverrides(descriptor)
        };
        foreach (var sink in _auditSinks)
        {
            await sink.WriteAsync(record, cancellationToken);
        }
    }

    private static IReadOnlyDictionary<string, INhAiToolVerifier> CreateVerifierRegistry(
        IEnumerable<INhAiToolVerifier> verifiers)
    {
        var registry = new Dictionary<string, INhAiToolVerifier>(StringComparer.Ordinal);
        foreach (var verifier in verifiers)
        {
            ArgumentNullException.ThrowIfNull(verifier);
            NhAiNames.ValidateSegment(verifier.Id, nameof(verifiers));
            if (!registry.TryAdd(verifier.Id, verifier))
            {
                throw new InvalidOperationException(
                    $"AI tool verifier '{verifier.Id}' is registered more than once.");
            }
        }
        return registry;
    }

    private static TimeSpan GetExecutionTimeout(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context)
    {
        if (context.Deadline is not { } deadline)
        {
            return descriptor.Timeout;
        }
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining < descriptor.Timeout ? remaining : descriptor.Timeout;
    }

    private static bool ExceedsResultLimit<T>(T? data, int maxResultBytes)
    {
        if (maxResultBytes < 1)
        {
            throw new InvalidOperationException(
                "AI tool result-size limit must be greater than zero.");
        }
        if (data is null)
        {
            return false;
        }
        return ExceedsSerializedLimit(data, maxResultBytes);
    }

    private static bool ExceedsInputLimit(object arguments, int maxInputBytes)
    {
        if (maxInputBytes < 1)
        {
            throw new InvalidOperationException(
                "AI tool input-size limit must be greater than zero.");
        }
        return ExceedsSerializedLimit(arguments, maxInputBytes);
    }

    private static bool ExceedsSerializedLimit(object value, int maximumBytes)
    {
        try
        {
            using var stream = new NhAiBoundedWriteStream(maximumBytes);
            JsonSerializer.Serialize(
                stream,
                value,
                value.GetType(),
                SerializerOptions);
            return false;
        }
        catch (NhAiSerializationLimitExceededException)
        {
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return true;
        }
    }

    private static string? SafeCode(string? code)
    {
        return NhAiNames.IsSegment(code) ? code : null;
    }

    private static TaskResult<T> WithFallbackCode<T>(TaskResult<T> result, string code)
    {
        var items = result.GetResultItems();
        if (items.Any(item => NhAiNames.IsSegment(item.Name)))
        {
            return result;
        }

        // Keep every message, but give keyless failures the stable pipeline code.
        var named = new TaskResult<T> { Data = result.Data };
        foreach (var item in items)
        {
            named.AddError(
                string.IsNullOrWhiteSpace(item.Name) ? code : item.Name,
                item.ErrorMessages);
        }
        if (!named.GetResultItems().Any(item => NhAiNames.IsSegment(item.Name)))
        {
            named.AddError(code, "The AI tool invocation failed.");
        }
        return named;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "AI tool {ToolId} v{ToolVersion} failed unexpectedly in invocation {InvocationId} with {ExceptionType}; the caller receives ai-tool-failed.")]
    private static partial void LogUnexpectedException(
        ILogger logger,
        string toolId,
        int toolVersion,
        Guid invocationId,
        string exceptionType);

    private sealed class InvocationTrace
    {
        public string? EvidenceReference { get; set; }
    }

    private static string? FirstResultCode(TaskResult result)
    {
        var name = result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        return SafeCode(name);
    }

    private sealed class NhAiBoundedWriteStream(int maximumBytes) : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateWrite(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ValidateWrite(buffer.Length);
        }

        private void ValidateWrite(int count)
        {
            if (count < 0 || _length > maximumBytes - (long)count)
            {
                throw new NhAiSerializationLimitExceededException();
            }
            _length += count;
        }
    }

    private sealed class NhAiSerializationLimitExceededException : Exception
    {
    }

    private static string? SafeEvidenceReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > 256
            || reference.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.' or ':' or '/')))
        {
            return null;
        }
        return reference;
    }

    private sealed class NhAiNoArguments
    {
        public static readonly NhAiNoArguments Instance = new();

        private NhAiNoArguments()
        {
        }
    }
}

internal static class NhAiTaskResultDataExtensions
{
    public static TaskResult<T> WithExecutionData<T>(this TaskResult<T> result, T? data)
    {
        result.Data = data;
        return result;
    }
}
