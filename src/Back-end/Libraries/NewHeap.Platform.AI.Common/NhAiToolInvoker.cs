using System.Diagnostics;
using System.Text.Json;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

public sealed class NhAiToolInvoker : INhAiToolInvoker
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
    private readonly INhAiIdempotencyManager _idempotencyManager;
    private readonly IReadOnlyDictionary<string, INhAiToolVerifier> _verifiers;
    private readonly INhAiCapabilityResolver _capabilityResolver;
    private readonly INhAiBudgetManager _budgetManager;
    private readonly INhAiToolConcurrencyLimiter _concurrencyLimiter;

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
        INhAiToolConcurrencyLimiter concurrencyLimiter)
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
        _idempotencyManager = idempotencyManager;
        _verifiers = CreateVerifierRegistry(verifiers);
        _capabilityResolver = capabilityResolver;
        _budgetManager = budgetManager;
        _concurrencyLimiter = concurrencyLimiter;
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
            return TaskResult<T>.Failed(authorization);
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
            return TaskResult<T>.Failed("AI tool input exceeded its configured size limit.");
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
                "The AI invocation lacks a required tool capability.");
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
            return TaskResult<T>.Failed("AI tool effect policy denied execution.");
        }
        if (effectDecision.Kind == NhAiEffectDecisionKind.RequireApproval)
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
                return TaskResult<T>.Failed("AI tool approval is required.");
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
                    cancellationToken);
                return TaskResult<T>.Failed(
                    $"AI tool approval validation failed with code '{validation.Code}'.");
            }
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
                cancellationToken);
            return TaskResult<T>.Failed("AI tool execution budget is exhausted.");
        }

        var reservation = await _budgetManager.ReserveAsync(
            new NhAiBudgetRequest(
                context.InvocationId,
                context.ModelProfileName ?? "tool-execution",
                1,
                0,
                0,
                null),
            cancellationToken);
        if (!reservation.Success)
        {
            activity?.SetTag("newheap.ai.tool.outcome", "budget-denied");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.BudgetExhausted,
                cancellationToken);
            return TaskResult<T>.Failed("AI tool execution budget could not be reserved.");
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
                cancellationToken);
            return TaskResult<T>.Failed("AI tool concurrency limit was reached.");
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
                cancellationToken);
            return TaskResult<T>.Failed("AI tool execution deadline has expired.");
        }

        NhAiIdempotencyLease? idempotencyLease = null;
        var idempotencyCompleted = false;
        if (descriptor.Idempotency == NhAiIdempotencySupport.Required
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
                    idempotencyCode: "idempotency-key-invalid");
                return TaskResult<T>.Failed(
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
            var idempotencyCode = SafeCode(idempotencyLease.Code);
            activity?.SetTag("newheap.ai.tool.idempotency", idempotencyCode);
            if (idempotencyLease.Decision != NhAiIdempotencyDecisionKind.Acquired)
            {
                activity?.SetTag("newheap.ai.tool.outcome", "idempotency-denied");
                await WriteAuditAsync(
                    descriptor,
                    context,
                    NhAiOutcomeKind.Conflict,
                    cancellationToken,
                    idempotencyCode: idempotencyCode);
                return TaskResult<T>.Failed(
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
                    idempotencyCode: SafeCode(idempotencyLease?.Code));
                return TaskResult<T>.Failed("AI tool result exceeded its configured size limit.");
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
                        idempotencyCode: SafeCode(idempotencyLease?.Code),
                        verificationCode: verificationCode,
                        verificationEvidenceReference: verificationEvidenceReference);
                    return TaskResult<T>
                        .Failed("AI tool execution completed, but independent verification failed.")
                        .WithExecutionData(result.Data);
                }
            }

            var outcome = result.Success
                ? NhAiOutcomeKind.Succeeded
                : NhAiOutcomeKind.TerminalFailure;
            await CompleteIdempotencyOnceAsync(outcome);
            activity?.SetTag("newheap.ai.tool.outcome", result.Success ? "succeeded" : "failed");
            await WriteAuditAsync(
                descriptor,
                context,
                outcome,
                cancellationToken,
                idempotencyCode: SafeCode(idempotencyLease?.Code),
                verificationCode: verificationCode,
                verificationEvidenceReference: verificationEvidenceReference);
            return result;
        }
        catch (OperationCanceledException)
        {
            await CompleteIdempotencyOnceAsync(NhAiOutcomeKind.TerminalFailure);
            activity?.SetTag("newheap.ai.tool.outcome", "cancelled");
            throw;
        }
        catch
        {
            await CompleteIdempotencyOnceAsync(NhAiOutcomeKind.TerminalFailure);
            activity?.SetTag("newheap.ai.tool.outcome", "exception");
            await WriteAuditAsync(
                descriptor,
                context,
                NhAiOutcomeKind.TerminalFailure,
                CancellationToken.None,
                idempotencyCode: SafeCode(idempotencyLease?.Code));
            throw;
        }
    }

    private async ValueTask WriteAuditAsync(
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext? context,
        NhAiOutcomeKind outcome,
        CancellationToken cancellationToken,
        string? idempotencyCode = null,
        string? verificationCode = null,
        string? verificationEvidenceReference = null)
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
            IdempotencyCode = idempotencyCode,
            VerificationCode = verificationCode,
            VerificationEvidenceReference = verificationEvidenceReference
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
