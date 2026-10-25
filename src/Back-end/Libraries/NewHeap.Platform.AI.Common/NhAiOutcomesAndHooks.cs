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
    TerminalFailure = 8,

    /// <summary>
    /// A governed deadline ended the call: the model profile's timeout or the invocation context's
    /// deadline, not the caller's cancellation. The governed chat executor reports it as
    /// <see cref="NhAiModelFailureCodes.Timeout"/>.
    /// </summary>
    TimedOut = 9
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

    /// <summary>
    /// Which approval step governed the invocation: the Platform validator code,
    /// <c>authoritative-evidence-validated</c> for consumer-validated evidence,
    /// <c>consumer-authoritative</c> when the tool validates approval inside the
    /// governed invocation, or the failure code of a rejected approval.
    /// </summary>
    public string? ApprovalCode { get; init; }

    /// <summary>
    /// Bounded evidence reference supplied by the authoritative execution evidence validator.
    /// </summary>
    public string? ApprovalEvidenceReference { get; init; }

    public string? IdempotencyCode { get; init; }
    public string? VerificationCode { get; init; }
    public string? VerificationEvidenceReference { get; init; }

    /// <summary>
    /// Bounded first failure code of a failed invocation result, so consumer-attested
    /// approval or replay outcomes remain visible in the audit trail.
    /// </summary>
    public string? ResultCode { get; init; }

    /// <summary>
    /// Bounded list of explicit protocol annotation hint overrides declared by the tool, such as
    /// <c>destructive-hint=true</c>, so a hint that differs from the effect remains reviewable.
    /// </summary>
    public string? AnnotationOverrides { get; init; }

    public NhAiActorKind ActorKind { get; init; } = NhAiActorKind.Human;

    /// <summary>
    /// The agent the invocation ran for, when it ran for an agent.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// The person or agent accountable for the invocation.
    /// </summary>
    public string? AccountableOwnerId { get; init; }

    public string? ProposalId { get; init; }

    /// <summary>
    /// Whether an agent grant or the initiator's own rights allowed the invocation.
    /// </summary>
    public NhAiGrantSource GrantSource { get; init; } = NhAiGrantSource.None;

    public NhAiRetentionCategory RetentionCategory { get; init; } = NhAiRetentionCategory.ToolActionAudit;
}

public interface INhAiAuditSink
{
    ValueTask WriteAsync(
        NhAiAuditRecord record,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The content-free usage of one governed model call. <see cref="InvocationId"/> is the id of the
/// invocation context the call ran in, which several calls share; <see cref="ModelCallId"/> names
/// this one call.
/// </summary>
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
    /// <summary>
    /// The id of this model call: new for every call, the same for its retries, and the one its
    /// budget reservation (<see cref="NhAiBudgetRequest.ModelCallId"/>) and its
    /// <c>ai.model.invoke</c> activity (<c>newheap.ai.model_call_id</c>) carry.
    /// </summary>
    public Guid ModelCallId { get; init; }

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
    decimal? RequestedEstimatedCost)
{
    /// <summary>The authenticated actor or accountable owner charged for this reservation.</summary>
    public string? ActorId { get; init; }

    /// <summary>
    /// The agent the call works for, when it works for one; in an assistant conversation the agent
    /// the person talks to.
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// The skill of <see cref="AgentId"/> the call works in, when the assistant knows it.
    /// </summary>
    public string? SkillId { get; init; }

    /// <summary>
    /// The agent run the call belongs to, when it belongs to one.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// What the call is for: one of <see cref="NhAiBudgetPurposes"/>.
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// The model call the reservation is for, the same id as its usage record's
    /// <see cref="NhAiUsageRecord.ModelCallId"/>; <see cref="Guid.Empty"/> for a reservation that is
    /// no model call, such as a tool call or embeddings.
    /// </summary>
    public Guid ModelCallId { get; init; }
}

/// <summary>
/// What a budget reservation is for, as <see cref="NhAiBudgetRequest.Purpose"/> names it, so a
/// budget manager can treat a person's chat, an agent's work and calls from outside apart.
/// </summary>
public static class NhAiBudgetPurposes
{
    /// <summary>A person's turn in an assistant conversation.</summary>
    public const string Chat = "chat";

    /// <summary>
    /// Work of an agent: a turn of an agent run by rules or a model, including triage, or a call
    /// for an agent outside the assistant.
    /// </summary>
    public const string AgentRun = "agent-run";

    /// <summary>An agent answering a consultation in another agent's thread.</summary>
    public const string Consultation = "consultation";

    /// <summary>An approved proposal that executes after people decided.</summary>
    public const string ProposalExecution = "proposal-execution";

    /// <summary>A tool call of an MCP client through the MCP export.</summary>
    public const string Mcp = "mcp";

    /// <summary>Embeddings of an ingestion.</summary>
    public const string Ingestion = "ingestion";

    /// <summary>
    /// A model or tool call the application makes itself through <see cref="INhAiChatExecutor"/> or
    /// <see cref="INhAiToolInvoker"/>, for no agent, such as a call through the API bridge.
    /// </summary>
    public const string Application = "application";

    /// <summary>
    /// The purpose of a model or tool call: the one its call site set for the current flow, otherwise
    /// agent work when it names an agent and the application's own call when it does not.
    /// </summary>
    internal static string Of(string? agentId)
    {
        return NhAiBudgetPurposeScope.Current
            ?? (agentId is null ? Application : AgentRun);
    }
}

/// <summary>
/// The budget purpose of the calls in the current asynchronous flow, for a call site that knows it
/// better than the invocation context, such as the MCP export.
/// </summary>
internal static class NhAiBudgetPurposeScope
{
    private static readonly AsyncLocal<string?> CurrentPurpose = new();

    public static string? Current => CurrentPurpose.Value;

    public static IDisposable Begin(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var previous = CurrentPurpose.Value;
        CurrentPurpose.Value = purpose;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentPurpose.Value = previous;
            }
        }
    }
}

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
