using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

public enum NhAiOutcomeKind
{
    Succeeded = 0,
    ValidationFailed = 1,
    NotFound = 2,
    AuthorizationDenied = 3,
    Conflict = 4,
    ApprovalRequired = 5,
    BudgetExhausted = 6,
    DependencyUnavailable = 7,
    TerminalFailure = 8
}

public sealed record NhAiOutcomeClassification(
    NhAiOutcomeKind Kind,
    string Code,
    bool Retryable = false);

public sealed record NhAiSafeOutcome(
    NhAiOutcomeKind Kind,
    string Code,
    bool Retryable,
    int ErrorCount);

public interface INhAiTaskResultMapper
{
    NhAiSafeOutcome Map(
        TaskResult result,
        NhAiOutcomeClassification? failureClassification = null);
}

public sealed class NhAiTaskResultMapper : INhAiTaskResultMapper
{
    public NhAiSafeOutcome Map(
        TaskResult result,
        NhAiOutcomeClassification? failureClassification = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Success)
        {
            return new NhAiSafeOutcome(NhAiOutcomeKind.Succeeded, "succeeded", false, 0);
        }

        var classification = failureClassification
            ?? new NhAiOutcomeClassification(
                NhAiOutcomeKind.TerminalFailure,
                "unclassified-failure");
        NhAiNames.ValidateSegment(classification.Code, nameof(failureClassification));
        var errorCount = result.GetResultItems().Sum(item => item.ErrorMessages.Count);
        return new NhAiSafeOutcome(
            classification.Kind,
            classification.Code,
            classification.Retryable,
            errorCount);
    }
}

public enum NhAiRetentionCategory
{
    Transient = 0,
    Operational = 1,
    Audit = 2,
    Evaluation = 3,
    EphemeralRequestContent = 10,
    ConversationContent = 20,
    ContextTraceMetadata = 30,
    ProposalApprovalEvidence = 40,
    ToolActionAudit = 50,
    OperationalTelemetry = 60,
    EvaluationArtifact = 70,
    UsageAggregate = 80
}

public sealed record NhAiAuditRecord(
    Guid InvocationId,
    string ToolId,
    int ToolVersion,
    string? ActorId,
    string? Purpose,
    NhAiOutcomeKind Outcome,
    DateTimeOffset Timestamp)
{
    public string? RunId { get; init; }
    public string? CorrelationId { get; init; }
    public string? ApprovalId { get; init; }
    public string? IdempotencyCode { get; init; }
    public string? VerificationCode { get; init; }
    public string? VerificationEvidenceReference { get; init; }
    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.ToolActionAudit;
}

public interface INhAiAuditSink
{
    ValueTask WriteAsync(
        NhAiAuditRecord record,
        CancellationToken cancellationToken = default);
}

public sealed record NhAiUsageRecord(
    Guid InvocationId,
    string ProfileName,
    int ProfileVersion,
    int InputTokens,
    int OutputTokens,
    decimal? EstimatedCost,
    TimeSpan Duration,
    DateTimeOffset Timestamp)
{
    public string? RunId { get; init; }
    public string? AgentId { get; init; }
    public string? ToolId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Purpose { get; init; }
    public string? FinishReason { get; init; }
    public string? ModelIdHash { get; init; }
    public string? PromptVersion { get; init; }
    public string? PromptHash { get; init; }
    public string? AgentVersion { get; init; }
    public string? CatalogVersion { get; init; }
    public string? CatalogHash { get; init; }
    public string? ContextHash { get; init; }
    public long? CachedInputTokens { get; init; }
    public TimeSpan? TimeToFirstToken { get; init; }
    public int InputCharacters { get; init; }
    public int OutputCharacters { get; init; }
    public NhAiOutcomeKind Outcome { get; init; } = NhAiOutcomeKind.Succeeded;
    public IReadOnlyList<NhAiUsageScope> ExecutionScopes { get; init; } = [];
    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.UsageAggregate;
}

public sealed record NhAiUsageScope(
    string Type,
    string Id);

public interface INhAiUsageSink
{
    ValueTask WriteAsync(
        NhAiUsageRecord record,
        CancellationToken cancellationToken = default);
}

public sealed record NhAiBudgetRequest(
    Guid InvocationId,
    string ProfileName,
    int RequestedCalls,
    int RequestedInputTokens,
    int RequestedOutputTokens,
    decimal? RequestedEstimatedCost);

public sealed record NhAiBudgetReservation(
    string ReservationId,
    NhAiModelBudget Remaining,
    DateTimeOffset ExpiresAt);

public interface INhAiBudgetManager
{
    ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class NhAiDenyBudgetManager : INhAiBudgetManager
{
    public ValueTask<TaskResult<NhAiBudgetReservation>> ReserveAsync(
        NhAiBudgetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            TaskResult<NhAiBudgetReservation>.Failed(
                "An AI budget manager is required for bounded execution."));
    }
}
